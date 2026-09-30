// Finds the mod manager's own latest release on GitHub and downloads
// its build for this edition.
//
// API used: https://api.github.com/repos/RTVmods/RTV-Mod-Manager/releases/latest
// Auth-free, rate-limited at 60 req/hr per IP for unauthenticated
// requests — well within our usage given the 24h cache.
//
// A release carries one asset per edition (VostokModManagerAI.exe and
// VostokModManagerIntegrated.exe, or a .zip holding one of them).

using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace VostokModManager.Api;

public record ManagerReleaseAsset
{
    public string Name { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }
}

public record ManagerRelease
{
    /// <summary>The release tag with any leading "v" removed.</summary>
    public string Version { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public IReadOnlyList<ManagerReleaseAsset> Assets { get; init; } = Array.Empty<ManagerReleaseAsset>();

    /// <summary>The asset for the edition whose executable is named
    /// `exeName` (e.g. "VostokModManagerAI.exe"): that exact file, or a
    /// .zip named after the edition. Null when the release has neither.</summary>
    public ManagerReleaseAsset? AssetFor(string exeName)
    {
        var stem = Path.GetFileNameWithoutExtension(exeName);
        return Assets.FirstOrDefault(a =>
                   string.Equals(a.Name, exeName, StringComparison.OrdinalIgnoreCase))
               ?? Assets.FirstOrDefault(a =>
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                   && a.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
    }
}

public enum ManagerReleaseStatus
{
    /// <summary>A release was found.</summary>
    Found,
    /// <summary>The repository has no published release.</summary>
    NoRelease,
    /// <summary>The check could not be completed (offline, rate limit,
    /// unexpected response).</summary>
    Failed,
}

public class ManagerReleaseChecker
{
    public const string RepoUrl = "https://github.com/RTVmods/RTV-Mod-Manager";
    public const string ReleasesUrl = RepoUrl + "/releases";
    private const string LatestUrl =
        "https://api.github.com/repos/RTVmods/RTV-Mod-Manager/releases/latest";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var ver = v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", $"VostokModManager/{ver} (+{RepoUrl})");
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    public async Task<(ManagerReleaseStatus Status, ManagerRelease? Release)> FetchLatestAsync(
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.SendAsync(req, timeout.Token);
            // GitHub answers 404 for "latest" when nothing is published.
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return (ManagerReleaseStatus.NoRelease, null);
            if (!resp.IsSuccessStatusCode)
                return (ManagerReleaseStatus.Failed, null);

            var json = await resp.Content.ReadAsStringAsync(timeout.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = Str(root, "tag_name").Trim();
            if (tag.Length == 0) return (ManagerReleaseStatus.Failed, null);

            var assets = new List<ManagerReleaseAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (name.Length == 0 || url.Length == 0) continue;
                    assets.Add(new ManagerReleaseAsset
                    {
                        Name = name,
                        DownloadUrl = url,
                        Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0,
                    });
                }
            }
            var html = Str(root, "html_url");
            return (ManagerReleaseStatus.Found, new ManagerRelease
            {
                Version = tag.TrimStart('v', 'V'),
                HtmlUrl = html.Length > 0 ? html : ReleasesUrl,
                Assets = assets,
            });
        }
        catch
        {
            return (ManagerReleaseStatus.Failed, null);
        }
    }

    /// <summary>Streams a release asset to `savePath`. Throws on a
    /// non-2xx response.</summary>
    public async Task DownloadAssetAsync(
        ManagerReleaseAsset asset, string savePath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(savePath);
        await stream.CopyToAsync(file, ct);
    }

    private static string Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";
}
