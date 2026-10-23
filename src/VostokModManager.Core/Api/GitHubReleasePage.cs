// The latest release's tag read from github.com itself, for when the
// REST API refuses: without a token it allows 60 requests an hour per
// IP, and a few manager starts plus anything else on the same connection
// use that up. The web page <repo>/releases/latest redirects to
// <repo>/releases/tag/<tag>, or to <repo>/releases when nothing is
// published, and that redirect is not counted against the API limit.

using System.Net.Http;

namespace VostokModManager.Api;

public enum LatestTagStatus
{
    Found,
    /// <summary>The repository has no published release.</summary>
    NoRelease,
    /// <summary>Offline, or an answer that isn't the expected redirect.</summary>
    Failed,
}

public static class GitHubReleasePage
{
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "VostokModManager (+" + ManagerReleaseChecker.RepoUrl + ")");
        return c;
    }

    /// <summary>The latest release's tag of the repository at `repoUrl`
    /// (https://github.com/owner/name). Never throws.</summary>
    public static async Task<(LatestTagStatus Status, string Tag)> FetchLatestTagAsync(
        string repoUrl, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                repoUrl.TrimEnd('/') + "/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
            return ReadRedirect(repoUrl, (int)resp.StatusCode, resp.Headers.Location);
        }
        catch
        {
            return (LatestTagStatus.Failed, "");
        }
    }

    /// <summary>What a /releases/latest answer says: a redirect to
    /// /releases/tag/&lt;tag&gt; names the release, one to /releases means
    /// there is none, anything else is a failed check.</summary>
    public static (LatestTagStatus Status, string Tag) ReadRedirect(string repoUrl, int statusCode, Uri? location)
    {
        if (statusCode is < 300 or >= 400 || location == null) return (LatestTagStatus.Failed, "");
        var target = location.IsAbsoluteUri ? location : new Uri(new Uri(repoUrl), location);
        var path = target.AbsolutePath.TrimEnd('/');
        const string marker = "/releases/tag/";
        var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var tag = Uri.UnescapeDataString(path[(i + marker.Length)..]);
            return tag.Length > 0 ? (LatestTagStatus.Found, tag) : (LatestTagStatus.Failed, "");
        }
        if (path.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
            return (LatestTagStatus.NoRelease, "");
        return (LatestTagStatus.Failed, "");
    }
}
