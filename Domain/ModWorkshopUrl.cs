// Tiny pure helpers for ModWorkshop URLs. Kept dependency-free and
// UI-free so it can be unit-tested without a browser. A mod page is
// modworkshop.net/mod/<id>[/optional-slug][?query][#frag]; the numeric
// id is what the public download API (DownloadLatestAsync) needs.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class ModWorkshopUrl
{
    private static readonly Regex _reModId = new(
        @"modworkshop\.net/mod/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when <paramref name="url"/> points at a specific
    /// ModWorkshop mod page; outputs the numeric mod id. Accepts full
    /// URLs, bare hosts, with or without scheme / trailing slug, and a
    /// bare numeric id typed by the user.</summary>
    public static bool TryParseModId(string url, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var s = url.Trim();

        // A bare number the user pasted is treated as the mod id.
        if (int.TryParse(s, out var direct) && direct > 0)
        {
            id = direct;
            return true;
        }

        var m = _reModId.Match(s);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed) && parsed > 0)
        {
            id = parsed;
            return true;
        }
        return false;
    }

    /// <summary>True if the URL is anywhere on modworkshop.net.</summary>
    public static bool IsModWorkshop(string url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains("modworkshop.net", StringComparison.OrdinalIgnoreCase);
}
