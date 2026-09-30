// HTTP client for vostokmods.net, the Road to Vostok mod host.
//
// The site publishes no API documentation. These are the JSON routes its
// own pages use, and the same ones the Metro Mod Loader (3.4+) calls:
//
//   GET /api/mods?page=&q=&sort=&taxonomies=&limit=
//         -> { entries: [...], total, page, pageCount }
//   GET /api/mods/<slug>
//         -> mod detail; versions[] is newest-first and each entry has
//            version, fileName, fileSize, downloadUrl, downloadable
//   GET /api/mods/<slug>/versions/<version>/download
//         -> 302 to the file on files.vostokmods.net (no login)
//   GET /api/taxonomies
//   GET /api/modpacks, /api/modpacks/<slug>/manifest
//
// Things to know:
//   - A mod is identified by its slug. There are no numeric ids.
//   - Versions only arrive with the mod detail, so a version check costs
//     one request per mod. Details are cached for 30 minutes.
//   - A version the host has not cleared for download has
//     downloadable=false. Those are skipped when resolving "latest".
//   - The response shape has changed before. A listing without `entries`
//     or a detail without `versions` is reported as an error, never as an
//     empty result: "no mods" and "could not read the mods" must not look
//     the same to the caller.

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using VostokModManager.Domain;

namespace VostokModManager.Api;

public enum VostokModsError
{
    /// <summary>No connection, DNS failure or timeout.</summary>
    Offline,
    /// <summary>The slug does not exist (HTTP 404).</summary>
    NotFound,
    /// <summary>The mod exists but has no file the host will serve.</summary>
    NoFile,
    /// <summary>The mod exists but not at the requested version.</summary>
    VersionNotFound,
    /// <summary>HTTP 429.</summary>
    RateLimited,
    /// <summary>Any other HTTP failure, or a body that is not the
    /// expected shape.</summary>
    BadResponse,
}

public class VostokModsException : Exception
{
    public VostokModsError Kind { get; }
    public int StatusCode { get; }

    public VostokModsException(VostokModsError kind, string message, int statusCode = 0, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}

/// <summary>A listing card, or the summary half of a mod detail.</summary>
public record VmModSummary
{
    /// <summary>The host's UUID for the mod. A mod file's own
    /// `source="vostokmods:..."` line may carry this instead of the
    /// slug; the API accepts either.</summary>
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string Category { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
    public string LatestGameVersion { get; init; } = "";
    public int Downloads { get; init; }
    public int Views { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    /// <summary>False when the newest version has not been cleared for
    /// download, so there is nothing to install yet.</summary>
    public bool HasDownloadableFile { get; init; }

    public ModSource Source => ModSource.ForSlug(Slug);
    public string PageUrl => VostokModsUrl.ModPageUrl(Slug);

    /// <summary>True when a source names this mod, by slug or by UUID.</summary>
    public bool Matches(ModSource source)
        => source.IsValid
           && source.Provider == ModSource.VostokMods
           && (string.Equals(source.Id, Slug, StringComparison.OrdinalIgnoreCase)
               || (Id.Length > 0 && string.Equals(source.Id, Id, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>One downloadable version of a mod.</summary>
public record VmFile
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Absolute URL. Redirects to the storage host.</summary>
    public string DownloadUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public DateTime? CreatedAt { get; init; }
    /// <summary>Markdown.</summary>
    public string Changelog { get; init; } = "";
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();
}

public record VmModDetail : VmModSummary
{
    /// <summary>Markdown.</summary>
    public string Description { get; init; } = "";
    public string License { get; init; } = "";
    /// <summary>Versions the host will serve, newest first.</summary>
    public IReadOnlyList<VmFile> Files { get; init; } = Array.Empty<VmFile>();
    /// <summary>The newest downloadable version, or "" when there is none.</summary>
    public string LatestVersion => Files.Count > 0 ? Files[0].Version : "";
}

public record VmPage<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageCount { get; init; }
    public bool HasMore => Page > 0 && PageCount > Page;
}

public record VmTaxonomy
{
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"categories" or "tags".</summary>
    public string GroupSlug { get; init; } = "";
    public string GroupName { get; init; } = "";
}

public record VmModpackSummary
{
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
    public int ModCount { get; init; }
    public int Downloads { get; init; }
    public DateTime? UpdatedAt { get; init; }

    public string PageUrl => VostokModsUrl.ModpackPageUrl(Slug);
}

public record VmModpackMod
{
    public int LoadOrder { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    /// <summary>False when the host cannot serve this mod; see Reason.</summary>
    public bool Available { get; init; } = true;
    public string Reason { get; init; } = "";
    public string Version { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    /// <summary>Lowercase hex, or "" when the host did not supply one.</summary>
    public string Sha256 { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
}

public record VmModpackManifest
{
    public int Format { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string Url { get; init; } = "";
    public string ManifestUrl { get; init; } = "";
    public string Hash { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public IReadOnlyList<VmModpackMod> Mods { get; init; } = Array.Empty<VmModpackMod>();
    /// <summary>The manifest's mcmConfig value as raw JSON, or "" when it
    /// is absent or null.</summary>
    public string McmConfigJson { get; init; } = "";
}

public class VostokModsClient
{
    /// <summary>Sort keys the listing accepts.</summary>
    public const string SortNewestFile = "newestFile";
    public const string SortUpdated = "updated";
    public const string SortDownloads = "downloads";
    public const string SortViews = "views";
    public const string SortNewest = "newest";

    /// <summary>The host rejects longer search strings.</summary>
    public const int QueryMaxLength = 100;
    /// <summary>The largest page the listing returns.</summary>
    public const int MaxPageSize = 100;

    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TaxonomyTtl = TimeSpan.FromMinutes(60);

    private static readonly HttpClient _http = CreateHttpClient();

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTime expires, string body)> _cache = new(StringComparer.Ordinal);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var ver = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"VostokModManager/{ver.Major}.{ver.Minor}.{ver.Build} (+https://github.com/RTVmods/RTV-Mod-Manager)");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return c;
    }

    // ── mods ──────────────────────────────────────────────────────────

    /// <summary>One page of the mod listing. <paramref name="limit"/> 0
    /// means the host's default page size (24).</summary>
    public async Task<VmPage<VmModSummary>> ListModsAsync(
        int page = 1,
        string? query = null,
        string? sort = null,
        string? taxonomies = null,
        int limit = 0,
        CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + Math.Max(1, page) };
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            if (q.Length > QueryMaxLength) q = q.Substring(0, QueryMaxLength);
            qs.Add("q=" + Uri.EscapeDataString(q));
        }
        if (!string.IsNullOrWhiteSpace(sort)) qs.Add("sort=" + Uri.EscapeDataString(sort));
        if (!string.IsNullOrWhiteSpace(taxonomies)) qs.Add("taxonomies=" + Uri.EscapeDataString(taxonomies));
        if (limit > 0) qs.Add("limit=" + Math.Min(limit, MaxPageSize));

        var url = VostokModsUrl.ApiBase + "/mods?" + string.Join("&", qs);
        var body = await GetJsonAsync(url, ListTtl, forceFresh: false, ct);
        using var doc = ParseJson(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent an unexpected mod listing.");

        var items = new List<VmModSummary>();
        foreach (var row in entries.EnumerateArray())
        {
            var s = ReadSummary(row);
            // A row with no slug can be neither opened nor downloaded.
            if (s.Slug.Length > 0) items.Add(s);
        }
        return new VmPage<VmModSummary>
        {
            Items = items,
            Total = Int(root, "total"),
            Page = Int(root, "page"),
            PageCount = Int(root, "pageCount"),
        };
    }

    /// <summary>Every mod on the host, fetched in the largest pages the
    /// listing allows.</summary>
    public async Task<List<VmModSummary>> ListAllModsAsync(CancellationToken ct = default)
    {
        var all = new List<VmModSummary>();
        // A guard against a listing that never reports its last page.
        const int maxPages = 100;
        for (var page = 1; page <= maxPages; page++)
        {
            var p = await ListModsAsync(page, limit: MaxPageSize, ct: ct);
            all.AddRange(p.Items);
            if (!p.HasMore || p.Items.Count == 0) break;
        }
        return all;
    }

    /// <summary>A mod's detail, including its downloadable versions.
    /// Throws <see cref="VostokModsException"/> with Kind NotFound when
    /// the slug does not exist.</summary>
    public async Task<VmModDetail> GetModAsync(
        string slug, bool forceFresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new VostokModsException(VostokModsError.NotFound, "No mod slug was given.");
        var url = VostokModsUrl.ApiBase + "/mods/" + Uri.EscapeDataString(slug.Trim());
        var body = await GetJsonAsync(url, DetailTtl, forceFresh, ct);
        using var doc = ParseJson(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent an unexpected mod detail.");

        var summary = ReadSummary(root);
        if (summary.Slug.Length == 0)
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent a mod detail with no slug.");

        var files = new List<VmFile>();
        if (root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in versions.EnumerateArray())
            {
                if (!Truthy(v, "downloadable")) continue;
                var f = ReadFile(v);
                if (f.DownloadUrl.Length > 0) files.Add(f);
            }
        }
        else
        {
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent a mod detail with no version list.");
        }

        return new VmModDetail
        {
            Id = summary.Id,
            Slug = summary.Slug,
            Name = summary.Name,
            Summary = summary.Summary,
            Author = summary.Author,
            Category = summary.Category,
            ThumbnailUrl = summary.ThumbnailUrl,
            LatestGameVersion = summary.LatestGameVersion,
            Downloads = summary.Downloads,
            Views = summary.Views,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            HasDownloadableFile = files.Count > 0,
            Description = Str(root, "description"),
            License = Str(root, "license"),
            Files = files,
        };
    }

    /// <summary>The file to download for a mod: the newest downloadable
    /// version, or exactly <paramref name="version"/> when one is given.</summary>
    public async Task<VmFile> ResolveFileAsync(
        string slug, string? version = null, bool forceFresh = false, CancellationToken ct = default)
    {
        var detail = await GetModAsync(slug, forceFresh, ct);
        if (!string.IsNullOrWhiteSpace(version))
        {
            var want = version.Trim();
            foreach (var f in detail.Files)
                if (string.Equals(f.Version, want, StringComparison.Ordinal))
                    return f;
            throw new VostokModsException(VostokModsError.VersionNotFound,
                $"Version {want} of '{slug}' is not available on VostokMods.", 404);
        }
        if (detail.Files.Count == 0)
            throw new VostokModsException(VostokModsError.NoFile,
                $"'{slug}' has no downloadable file on VostokMods.");
        return detail.Files[0];
    }

    /// <summary>The newest downloadable version of each mod, keyed by
    /// slug. One request per mod that is not already cached. Stops early
    /// when the host rate-limits or the connection drops, returning what
    /// it learned so far. Throws when it learned nothing at all, so a
    /// failed check is never mistaken for "everything is up to date".</summary>
    public async Task<Dictionary<string, string>> CheckVersionsAsync(
        IEnumerable<string> slugs,
        bool forceFresh = false,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var list = slugs
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        VostokModsException? lastFailure = null;
        var failures = 0;
        var done = 0;

        foreach (var slug in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var file = await ResolveFileAsync(slug, null, forceFresh, ct);
                if (file.Version.Length > 0) versions[slug] = file.Version;
            }
            catch (VostokModsException ex)
            {
                failures++;
                lastFailure = ex;
                // Neither of these clears up mid-loop.
                if (ex.Kind == VostokModsError.RateLimited || ex.Kind == VostokModsError.Offline)
                {
                    if (versions.Count == 0) throw;
                    break;
                }
            }
            done++;
            progress?.Report((done, list.Count));
        }

        if (versions.Count == 0 && failures > 0)
            throw new VostokModsException(
                lastFailure?.Kind ?? VostokModsError.BadResponse,
                $"Could not read a version for any of the {failures} mod(s) checked.",
                lastFailure?.StatusCode ?? 0,
                lastFailure);
        return versions;
    }

    // ── downloads ─────────────────────────────────────────────────────

    /// <summary>Downloads a resolved file to <paramref name="savePath"/>,
    /// streaming to disk. The redirect to the storage host is followed.</summary>
    public async Task DownloadFileAsync(VmFile file, string savePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(file.DownloadUrl))
            throw new VostokModsException(VostokModsError.NoFile, "That version has no download URL.");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new VostokModsException(VostokModsError.Offline,
                "Could not reach VostokMods. Check your connection.", 0, ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw FromStatus(resp.StatusCode, "download", null);

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(savePath))
                await stream.CopyToAsync(output, ct);
        }

        if (new FileInfo(savePath).Length == 0)
        {
            try { File.Delete(savePath); } catch { }
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent an empty file.");
        }
    }

    /// <summary>Resolves and downloads the newest version of a mod (or
    /// the given version). Returns the file that was downloaded, so the
    /// caller knows its version and original file name.</summary>
    public async Task<VmFile> DownloadAsync(
        string slug, string savePath, string? version = null, CancellationToken ct = default)
    {
        // Resolve against fresh data: a cached detail may name a version
        // the author has since replaced.
        var file = await ResolveFileAsync(slug, version, forceFresh: true, ct);
        await DownloadFileAsync(file, savePath, ct);
        return file;
    }

    // ── taxonomies and modpacks ───────────────────────────────────────

    public async Task<List<VmTaxonomy>> ListTaxonomiesAsync(CancellationToken ct = default)
    {
        var body = await GetJsonAsync(VostokModsUrl.ApiBase + "/taxonomies", TaxonomyTtl, false, ct);
        using var doc = ParseJson(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent an unexpected category list.");
        var result = new List<VmTaxonomy>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var slug = Str(r, "slug");
            if (slug.Length == 0) continue;
            var groupSlug = "";
            var groupName = "";
            if (r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                groupSlug = Str(g, "slug");
                groupName = Str(g, "name");
            }
            result.Add(new VmTaxonomy
            {
                Slug = slug,
                Name = Str(r, "name"),
                GroupSlug = groupSlug,
                GroupName = groupName,
            });
        }
        return result;
    }

    public async Task<VmPage<VmModpackSummary>> ListModpacksAsync(
        int page = 1, string? query = null, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + Math.Max(1, page) };
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            if (q.Length > QueryMaxLength) q = q.Substring(0, QueryMaxLength);
            qs.Add("q=" + Uri.EscapeDataString(q));
        }
        var url = VostokModsUrl.ApiBase + "/modpacks?" + string.Join("&", qs);
        var body = await GetJsonAsync(url, ListTtl, false, ct);
        using var doc = ParseJson(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent an unexpected modpack listing.");

        var items = new List<VmModpackSummary>();
        foreach (var r in entries.EnumerateArray())
        {
            var slug = Str(r, "slug");
            if (slug.Length == 0) continue;
            items.Add(new VmModpackSummary
            {
                Slug = slug,
                Name = Str(r, "name"),
                Summary = Str(r, "summary"),
                Author = Str(r, "ownerDisplayName"),
                ThumbnailUrl = Str(r, "thumbnailUrl"),
                ModCount = Int(r, "modCount"),
                Downloads = Int(r, "downloadsCount"),
                UpdatedAt = Date(r, "updatedAt"),
            });
        }
        return new VmPage<VmModpackSummary>
        {
            Items = items,
            Total = Int(root, "total"),
            Page = Int(root, "page"),
            PageCount = Int(root, "pageCount"),
        };
    }

    /// <summary>A hosted modpack's manifest: its mods with pinned
    /// versions, load order and download URLs.</summary>
    public async Task<VmModpackManifest> GetModpackManifestAsync(
        string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new VostokModsException(VostokModsError.NotFound, "No modpack slug was given.");
        var manifestUrl = VostokModsUrl.ApiBase + "/modpacks/" + Uri.EscapeDataString(slug.Trim()) + "/manifest";
        // Not cached: a pack is applied straight after it is fetched, and a
        // stale manifest would pin versions the author has moved on from.
        var body = await GetJsonAsync(manifestUrl, TimeSpan.Zero, true, ct);
        using var doc = ParseJson(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("mods", out var modsEl)
            || modsEl.ValueKind != JsonValueKind.Array)
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent an unexpected modpack manifest.");

        var mods = new List<VmModpackMod>();
        foreach (var m in modsEl.EnumerateArray())
        {
            var modSlug = Str(m, "slug");
            if (modSlug.Length == 0) continue;
            var sha = Str(m, "sha256").ToLowerInvariant();
            mods.Add(new VmModpackMod
            {
                LoadOrder = Int(m, "loadOrder"),
                Slug = modSlug,
                Name = Str(m, "name"),
                Author = Str(m, "ownerDisplayName"),
                // Absent means available; only an explicit false is not.
                Available = !m.TryGetProperty("available", out var av) || IsTruthy(av),
                Reason = Str(m, "reason"),
                Version = Str(m, "version"),
                FileName = Str(m, "fileName"),
                FileSize = Long(m, "fileSize"),
                Sha256 = sha.Length == 64 ? sha : "",
                DownloadUrl = VostokModsUrl.Absolute(Str(m, "downloadUrl")),
            });
        }

        var mcm = "";
        if (root.TryGetProperty("mcmConfig", out var mcmEl)
            && mcmEl.ValueKind != JsonValueKind.Null
            && mcmEl.ValueKind != JsonValueKind.Undefined)
            mcm = mcmEl.GetRawText();

        return new VmModpackManifest
        {
            Format = Int(root, "format"),
            Slug = Str(root, "slug"),
            Name = Str(root, "name"),
            Summary = Str(root, "summary"),
            Author = Str(root, "ownerDisplayName"),
            Url = Str(root, "url"),
            ManifestUrl = manifestUrl,
            Hash = Str(root, "hash"),
            UpdatedAt = Str(root, "updatedAt"),
            Mods = mods,
            McmConfigJson = mcm,
        };
    }

    /// <summary>Drops a mod's cached detail, so the next read is fresh.</summary>
    public void Invalidate(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        var url = VostokModsUrl.ApiBase + "/mods/" + Uri.EscapeDataString(slug.Trim());
        lock (_cacheLock) _cache.Remove(url);
    }

    // ── transport ─────────────────────────────────────────────────────

    private async Task<string> GetJsonAsync(string url, TimeSpan ttl, bool forceFresh, CancellationToken ct)
    {
        if (!forceFresh && ttl > TimeSpan.Zero)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(url, out var hit) && hit.expires > DateTime.UtcNow)
                    return hit.body;
            }
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new VostokModsException(VostokModsError.Offline,
                "Could not reach VostokMods. Check your connection.", 0, ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw FromStatus(resp.StatusCode, "request", body);

            if (ttl > TimeSpan.Zero)
            {
                lock (_cacheLock)
                    _cache[url] = (DateTime.UtcNow + ttl, body);
            }
            return body;
        }
    }

    private static VostokModsException FromStatus(HttpStatusCode status, string what, string? body)
    {
        var code = (int)status;
        // Error bodies look like {"error":true,"statusCode":404,"message":"Mod not found"}.
        var hostMessage = "";
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                hostMessage = Str(doc.RootElement, "message");
            }
            catch { /* not JSON; fall back to the status code */ }
        }

        if (status == HttpStatusCode.NotFound)
            return new VostokModsException(VostokModsError.NotFound,
                hostMessage.Length > 0 ? hostMessage : "Not found on VostokMods.", code);
        if (code == 429)
            return new VostokModsException(VostokModsError.RateLimited,
                "VostokMods is rate-limiting requests. Try again in a few minutes.", code);
        return new VostokModsException(VostokModsError.BadResponse,
            hostMessage.Length > 0
                ? $"VostokMods {what} failed: {hostMessage} (HTTP {code})."
                : $"VostokMods {what} failed (HTTP {code}).",
            code);
    }

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new VostokModsException(VostokModsError.BadResponse,
                "VostokMods sent a response that is not valid JSON.", 0, ex);
        }
    }

    // ── normalizers ───────────────────────────────────────────────────

    /// <summary>A listing row and a detail object share these field names.</summary>
    private static VmModSummary ReadSummary(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return new VmModSummary();
        var slug = Str(row, "slug");
        var name = Str(row, "name");

        var gameVersion = "";
        if (row.TryGetProperty("latestGameVersion", out var gv) && gv.ValueKind == JsonValueKind.Object)
            gameVersion = Str(gv, "label");

        // Cards carry the newest version's downloadable flag; a detail
        // carries the full version list instead.
        var downloadable = false;
        if (row.TryGetProperty("latestVersion", out var lv) && lv.ValueKind == JsonValueKind.Object)
            downloadable = Truthy(lv, "downloadable");

        return new VmModSummary
        {
            Id = Str(row, "id"),
            Slug = slug,
            Name = name.Length > 0 ? name : slug,
            Summary = Str(row, "summary"),
            Author = Str(row, "ownerDisplayName"),
            Category = PrimaryCategory(row),
            ThumbnailUrl = Str(row, "thumbnailUrl"),
            LatestGameVersion = gameVersion,
            Downloads = Int(row, "downloadsCount"),
            Views = Int(row, "viewsCount"),
            CreatedAt = Date(row, "createdAt"),
            UpdatedAt = Date(row, "updatedAt"),
            HasDownloadableFile = downloadable,
        };
    }

    /// <summary>The taxonomies array mixes categories and tags; the
    /// group says which. Prefers a category, else the first entry.</summary>
    private static string PrimaryCategory(JsonElement row)
    {
        if (!row.TryGetProperty("taxonomies", out var tax) || tax.ValueKind != JsonValueKind.Array)
            return "";
        var firstAny = "";
        foreach (var t in tax.EnumerateArray())
        {
            var name = Str(t, "name");
            if (name.Length == 0) continue;
            if (firstAny.Length == 0) firstAny = name;
            if (t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.Object
                && Str(g, "slug").StartsWith("categor", StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return firstAny;
    }

    private static VmFile ReadFile(JsonElement v)
    {
        var gameVersions = new List<string>();
        if (v.TryGetProperty("gameVersions", out var gvs) && gvs.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in gvs.EnumerateArray())
            {
                var label = Str(g, "label");
                if (label.Length > 0) gameVersions.Add(label);
            }
        }
        return new VmFile
        {
            Id = Str(v, "id"),
            Version = Str(v, "version"),
            DownloadUrl = VostokModsUrl.Absolute(Str(v, "downloadUrl")),
            FileName = Path.GetFileName(Str(v, "fileName")),
            Size = Long(v, "fileSize"),
            CreatedAt = Date(v, "createdAt"),
            Changelog = Str(v, "changelog"),
            GameVersions = gameVersions,
        };
    }

    // ── JSON helpers (null and wrong-typed values read as empty) ──────

    private static string Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        if (!obj.TryGetProperty(name, out var p)) return "";
        return p.ValueKind switch
        {
            JsonValueKind.String => (p.GetString() ?? "").Trim(),
            JsonValueKind.Number => p.GetRawText(),
            _ => "",
        };
    }

    private static long Long(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0;
        if (!obj.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetInt64(out var l)) return l;
            if (p.TryGetDouble(out var d)) return (long)d;
        }
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var parsed))
            return parsed;
        return 0;
    }

    private static int Int(JsonElement obj, string name)
    {
        var l = Long(obj, name);
        return l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
    }

    private static bool Truthy(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var p)
           && IsTruthy(p);

    private static bool IsTruthy(JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => p.TryGetDouble(out var d) && d != 0,
        JsonValueKind.String => string.Equals(p.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static DateTime? Date(JsonElement obj, string name)
    {
        var s = Str(obj, name);
        if (s.Length == 0) return null;
        return DateTime.TryParse(
            s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d
            : null;
    }
}
