// Minimal HTTP client for the public ModWorkshop API.
//
// Quirks worth knowing:
//   - /mods/versions is GET with a JSON body (POST 405s). Verified
//     live during the Godot iteration; the endpoint accepts batches
//     of up to 100 mod_ids and returns {id_string: version_string}.
//   - /mods/{id}/files/latest/download returns a 302 to the storage
//     CDN URL. HttpClient follows redirects by default.
//   - Auth: none required for the read endpoints we use. Site terms
//     forbid spamming + replicating the site, so we only call when
//     the user explicitly asks (open / refresh / per-mod update).

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace VostokModManager.Api;

public record ModDetails
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
}

public class ModWorkshopClient
{
    private const string BASE_URL = "https://api.modworkshop.net";
    private const string USER_AGENT =
        "VostokModManager/0.3.0 (+https://modworkshop.net/g/roadtovostok)";
    private const int BATCH_LIMIT = 100;

    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // ParseAdd is strict about RFC 7231 syntax; the URL-in-parens
        // form is technically a valid comment but some validators
        // disagree. TryAddWithoutValidation sidesteps any nitpick.
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", USER_AGENT);
        return c;
    }

    /// <summary>Batch version check via /mods/versions.
    /// Returns dict of mod_id → latest version string. The API will
    /// be queried for at most 100 ids; extras are silently dropped.</summary>
    public async Task<Dictionary<int, string>> CheckVersionsAsync(
        IEnumerable<int> modIds,
        CancellationToken ct = default)
    {
        var ids = modIds
            .Where(i => i > 0)
            .Distinct()
            .Take(BATCH_LIMIT)
            .ToList();
        if (ids.Count == 0) return new();

        var bodyJson = JsonSerializer.Serialize(new { mod_ids = ids });
        var req = new HttpRequestMessage(HttpMethod.Get, $"{BASE_URL}/mods/versions")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        // {"56398":"0.6.1","55984":"2.2.4",...}
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (int.TryParse(prop.Name, out var id))
                result[id] = prop.Value.GetString() ?? "";
        }
        return result;
    }

    /// <summary>Fetches a single mod's metadata (name, description,
    /// author) from /mods/&lt;id&gt;. Defensive about field names —
    /// ModWorkshop's response shape isn't formally documented, so we
    /// try a few sensible aliases per field and fall back to empty
    /// when nothing matches. Throws on non-2xx HTTP.</summary>
    public async Task<ModDetails> GetModDetailsAsync(
        int modId,
        CancellationToken ct = default)
    {
        if (modId <= 0)
            throw new ArgumentException("modId must be > 0", nameof(modId));
        var url = $"{BASE_URL}/mods/{modId}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ModDetails
        {
            Id = modId,
            Name = TryString(root, "name") ?? "",
            Description = TryString(root, "description")
                       ?? TryString(root, "summary")
                       ?? TryString(root, "body")
                       ?? TryString(root, "long_description")
                       ?? "",
            Author = TryString(root, "author")
                  ?? TryString(root, "user_name")
                  ?? TryString(root, "username")
                  ?? "",
        };
    }

    private static string? TryString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Downloads the latest .vmz for a mod to `savePath`.
    /// Streams to disk so we don't keep a 5MB+ archive in memory.
    /// The 302 redirect from /files/latest/download to the storage
    /// CDN is followed automatically.</summary>
    public async Task DownloadLatestAsync(
        int modId,
        string savePath,
        CancellationToken ct = default)
    {
        if (modId <= 0)
            throw new ArgumentException("modId must be > 0", nameof(modId));

        var url = $"{BASE_URL}/mods/{modId}/files/latest/download";
        using var resp = await _http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        resp.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(savePath);
        await stream.CopyToAsync(file, ct);
    }
}
