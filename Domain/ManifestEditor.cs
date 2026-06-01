// Targeted edits for a mod.txt (Godot ConfigFile) string. We don't
// re-serialize the parsed manifest because that would lose comments,
// blank lines, and key ordering — the user's original formatting
// matters when they read the file again. Instead we splice the
// minimum number of lines we need to change.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class ManifestEditor
{
    /// <summary>Sets `[section] key = value` in a mod.txt string,
    /// preserving comments, blank lines, and the file's CRLF/LF
    /// style. Three cases:
    ///   1. The key already exists in the section — replace its value.
    ///   2. The section exists but no such key — insert right after
    ///      the section header.
    ///   3. No section — append it (with a blank-line separator if
    ///      the file doesn't end in one already).
    /// Section + key matching are case-insensitive (Godot ConfigFile
    /// is case-sensitive in practice but lower-case is the
    /// convention; we match either to be forgiving).</summary>
    public static string SetValue(string modTxt, string section, string key, string value)
    {
        var newline = modTxt.Contains("\r\n") ? "\r\n" : "\n";
        var lines = modTxt.Replace("\r\n", "\n").Split('\n').ToList();
        var sectionHeader = $"[{section}]";
        var keyRegex = new Regex(
            $@"^{Regex.Escape(key)}\s*=",
            RegexOptions.IgnoreCase);

        int sectionStart = -1;
        int keyLineIdx = -1;
        bool inSection = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]") && t.Length >= 2)
            {
                if (inSection) inSection = false;
                if (string.Equals(t, sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                    inSection = true;
                }
            }
            else if (inSection && keyRegex.IsMatch(t))
            {
                keyLineIdx = i;
            }
        }

        var newLine = $"{key} = {value}";
        if (keyLineIdx >= 0)
        {
            lines[keyLineIdx] = newLine;
        }
        else if (sectionStart >= 0)
        {
            lines.Insert(sectionStart + 1, newLine);
        }
        else
        {
            // Trim a single trailing blank line so the appended
            // section starts cleanly. Then add a blank separator and
            // the new section.
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(newLine);
        }
        // Preserve a trailing newline if the original had one.
        var result = string.Join(newline, lines);
        if (modTxt.EndsWith("\n") && !result.EndsWith(newline))
            result += newline;
        return result;
    }

    /// <summary>Convenience wrapper for `[updates] modworkshop = N`.</summary>
    public static string SetUpdatesModworkshop(string modTxt, int modworkshopId)
        => SetValue(modTxt, "updates", "modworkshop", modworkshopId.ToString());

    /// <summary>Convenience wrapper for `[mod] priority = N`.</summary>
    public static string SetModPriority(string modTxt, int priority)
        => SetValue(modTxt, "mod", "priority", priority.ToString());

    /// <summary>Deletes every line that belongs to `[section]` —
    /// the header line itself plus everything up to the next
    /// section header (or end-of-file). Used to wipe a section
    /// before rewriting it from a fresh dictionary, so stale
    /// entries from an earlier write don't survive. Preserves
    /// the rest of the file verbatim.</summary>
    public static string RemoveSection(string modTxt, string section)
    {
        var newline = modTxt.Contains("\r\n") ? "\r\n" : "\n";
        var lines = modTxt.Replace("\r\n", "\n").Split('\n').ToList();
        var header = $"[{section}]";
        int start = -1, end = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (start < 0)
            {
                if (string.Equals(t, header, StringComparison.OrdinalIgnoreCase))
                    start = i;
            }
            else if (t.StartsWith("[") && t.EndsWith("]") && t.Length >= 2)
            {
                end = i;
                break;
            }
        }
        if (start < 0) return modTxt;
        // Also drop one trailing blank line between this section
        // and the next so we don't accumulate blank padding across
        // repeated removes.
        if (end < lines.Count
            && start > 0
            && lines[start - 1].Trim().Length == 0)
            start--;
        lines.RemoveRange(start, end - start);
        var result = string.Join(newline, lines);
        if (modTxt.EndsWith("\n") && !result.EndsWith(newline)) result += newline;
        return result;
    }

    /// <summary>Rewrites `[dependency_sources]` from scratch using
    /// `sources` (mod_id → MW numeric id). Removes the old
    /// section first so deletions take effect — without that,
    /// keys removed between packs would stick around. Empty
    /// `sources` drops the section entirely, keeping mod.txt
    /// clean when the user hasn't recorded any MW ids for deps.
    /// </summary>
    public static string SetDependencySourcesSection(
        string modTxt,
        IReadOnlyDictionary<string, int> sources)
    {
        modTxt = RemoveSection(modTxt, "dependency_sources");
        if (sources.Count == 0) return modTxt;
        // Stable ordering so the file diffs cleanly across packs
        // — git-friendly + comparison-friendly for the user.
        foreach (var kvp in sources.OrderBy(
            k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            modTxt = SetValue(modTxt, "dependency_sources",
                kvp.Key, kvp.Value.ToString());
        }
        return modTxt;
    }

    /// <summary>Convenience wrapper for `[dependencies] required/optional`.
    /// `kind` is "required" or "optional"; mod IDs are written as a
    /// Godot ConfigFile string array — `["a", "b", "c"]` — because
    /// the in-game mod loader uses Godot's strict ConfigFile parser
    /// and rejects the whole file if any value isn't a valid
    /// Variant. (Bare CSV is the obvious-looking format but Godot's
    /// parser fails on it, which would then prevent the game from
    /// reading priority/autoload/hooks for the affected mod.)
    /// Empty list writes `[]` so the section round-trips cleanly
    /// — equivalent to no required deps.</summary>
    public static string SetDependencyList(string modTxt, string kind, IEnumerable<string> modIds)
    {
        var quoted = modIds.Select(id => "\"" + id.Replace("\"", "\\\"") + "\"");
        return SetValue(modTxt, "dependencies", kind, "[" + string.Join(", ", quoted) + "]");
    }

    private static readonly Regex _modWorkshopUrlRe = new(
        @"modworkshop\.net/mods?/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses a user-entered ModWorkshop reference. Accepts:
    ///   - bare numeric id ("56398")
    ///   - full URL ("https://modworkshop.net/mod/56398/some-slug")
    ///   - URL fragments ("modworkshop.net/mod/56398")
    /// Returns 0 if nothing valid was found.</summary>
    public static int ParseModWorkshopIdInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 0;
        input = input.Trim();
        if (int.TryParse(input, out var n) && n > 0) return n;
        var m = _modWorkshopUrlRe.Match(input);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && id > 0)
            return id;
        return 0;
    }
}
