// Pure helpers for vostokmods.net URLs. No UI and no network, so they can
// be tested on their own.
//
// A mod page is vostokmods.net/mod/<slug> (singular "mod"); the JSON API
// lists under /api/mods/<slug> (plural). The slug is what every API call
// and every `source="vostokmods:<slug>"` line carries.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class VostokModsUrl
{
    public const string SiteBase = "https://vostokmods.net";
    public const string ApiBase = "https://vostokmods.net/api";
    public const string ExploreModsUrl = "https://vostokmods.net/explore/mods";
    public const string ExploreModpacksUrl = "https://vostokmods.net/explore/modpacks";

    // The page route (/mod/<slug>) or the API route (/api/mods/<slug>).
    private static readonly Regex _reModUrl = new(
        @"vostokmods\.net/(?:api/mods|mod)/([A-Za-z0-9][A-Za-z0-9._~-]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _reModpackUrl = new(
        @"vostokmods\.net/(?:api/modpacks|modpack)/([A-Za-z0-9][A-Za-z0-9._~-]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // What a slug typed on its own looks like.
    private static readonly Regex _reBareSlug = new(
        @"^[a-z0-9][a-z0-9-]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Extracts a mod slug from user input. Accepts a mod page
    /// URL (with or without scheme, trailing path, query or fragment), a
    /// "vostokmods:&lt;slug&gt;" source key, or a slug typed on its own.
    /// The slug is returned lowercase.</summary>
    public static bool TryParseSlug(string? input, out string slug)
    {
        slug = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        var m = _reModUrl.Match(s);
        if (m.Success)
        {
            slug = m.Groups[1].Value.ToLowerInvariant();
            return true;
        }

        if (ModSource.TryParse(s, out var src) && src.Provider == ModSource.VostokMods)
        {
            slug = src.Id.ToLowerInvariant();
            return true;
        }

        // A bare slug. Anything that looks like a URL or a path is not one.
        if (s.IndexOf('/') < 0 && s.IndexOf('.') < 0 && _reBareSlug.IsMatch(s))
        {
            slug = s.ToLowerInvariant();
            return true;
        }
        return false;
    }

    /// <summary>True only when the URL is a specific mod's page, which
    /// is when the browser's Install button applies.</summary>
    public static bool TryParseModPageSlug(string? url, out string slug)
    {
        slug = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = _reModUrl.Match(url);
        if (!m.Success) return false;
        slug = m.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    /// <summary>Extracts a modpack slug from a modpack page or API URL.</summary>
    public static bool TryParseModpackSlug(string? url, out string slug)
    {
        slug = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = _reModpackUrl.Match(url);
        if (!m.Success) return false;
        slug = m.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    /// <summary>True if the URL is anywhere on vostokmods.net.</summary>
    public static bool IsVostokMods(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains("vostokmods.net", StringComparison.OrdinalIgnoreCase);

    public static string ModPageUrl(string slug)
        => string.IsNullOrEmpty(slug) ? "" : SiteBase + "/mod/" + Uri.EscapeDataString(slug);

    public static string ModpackPageUrl(string slug)
        => string.IsNullOrEmpty(slug) ? "" : SiteBase + "/modpack/" + Uri.EscapeDataString(slug);

    /// <summary>Turns a site-root-relative path from the API (download
    /// URLs arrive as "/api/mods/...") into an absolute URL.</summary>
    public static string Absolute(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return "";
        var s = pathOrUrl.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        return SiteBase + (s.StartsWith("/") ? s : "/" + s);
    }
}
