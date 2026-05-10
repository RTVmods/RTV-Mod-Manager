// Polls GitHub for the latest MML (Vostok Mod Loader) release. We
// don't have a reliable way to read the installed MML version from
// disk (it ships as a Godot .pck whose internal constants would
// need pck-walking to extract), so for now we surface the latest
// available release + a click-through to the releases page; the
// user compares to whatever they installed.
//
// API used: https://api.github.com/repos/ametrocavich/vostok-mod-loader/releases/latest
// Auth-free, but rate-limited at 60 req/hr per IP for unauthenticated
// requests — well within our usage given the 24h cache.

using System.Net.Http;
using System.Text.Json;

namespace VostokModManager.Api;

public record MmlRelease
{
    public string TagName { get; init; } = "";
    public DateTime? PublishedAt { get; init; }
    public string HtmlUrl { get; init; } = "";
}

public class MmlVersionChecker
{
    private const string LATEST_URL =
        "https://api.github.com/repos/ametrocavich/vostok-mod-loader/releases/latest";
    private const string USER_AGENT =
        "VostokModManager/0.3.0 (+https://modworkshop.net/g/roadtovostok)";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", USER_AGENT);
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/vnd.github+json");
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>Fetches the latest MML release. Returns null on
    /// network/parse failure — caller decides whether to fall back
    /// to a cached value or surface the error.</summary>
    public async Task<MmlRelease?> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LATEST_URL, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new MmlRelease
            {
                TagName = TryString(root, "tag_name") ?? "",
                PublishedAt = TryDate(root, "published_at"),
                HtmlUrl = TryString(root, "html_url")
                    ?? "https://github.com/ametrocavich/vostok-mod-loader/releases",
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        return obj.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private static DateTime? TryDate(JsonElement obj, string name)
    {
        var s = TryString(obj, name);
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(
            s, null, System.Globalization.DateTimeStyles.RoundtripKind,
            out var d) ? d : null;
    }
}
