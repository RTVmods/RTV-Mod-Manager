// Targeted edits for a mod.txt (Godot ConfigFile) string. We don't
// re-serialize the parsed manifest because that would lose comments,
// blank lines, and key ordering — the user's original formatting
// matters when they read the file again. Instead we splice the
// minimum number of lines we need to change.

using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class ManifestEditor
{
    /// <summary>Sets `[updates] modworkshop = N` in a mod.txt string.
    /// Three cases:
    ///   1. The key already exists — replace its value.
    ///   2. The [updates] section exists but no modworkshop key —
    ///      insert the key right after the section header.
    ///   3. No [updates] section — append it (with a blank-line
    ///      separator if the file doesn't end in one already).
    /// Preserves the rest of the file verbatim, including comments
    /// and Windows vs Unix line endings (it sniffs whichever the
    /// input uses and writes back the same).</summary>
    public static string SetUpdatesModworkshop(string modTxt, int modworkshopId)
    {
        var newline = modTxt.Contains("\r\n") ? "\r\n" : "\n";
        var lines = modTxt.Replace("\r\n", "\n").Split('\n').ToList();

        // Find the [updates] section's bounds and the modworkshop
        // line within it (if any). Sections end where the next
        // [section] header starts, or at EOF.
        int updatesStart = -1;
        int updatesEnd = lines.Count;
        int mwLineIdx = -1;
        bool inUpdates = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]") && t.Length >= 2)
            {
                if (inUpdates) { updatesEnd = i; inUpdates = false; }
                if (string.Equals(t, "[updates]", StringComparison.OrdinalIgnoreCase))
                {
                    updatesStart = i;
                    inUpdates = true;
                }
            }
            else if (inUpdates && Regex.IsMatch(t, @"^modworkshop\s*="))
            {
                mwLineIdx = i;
            }
        }

        var newLine = $"modworkshop = {modworkshopId}";
        if (mwLineIdx >= 0)
        {
            lines[mwLineIdx] = newLine;
        }
        else if (updatesStart >= 0)
        {
            lines.Insert(updatesStart + 1, newLine);
        }
        else
        {
            // Trim a single trailing blank line so the appended
            // section starts cleanly. Then add a blank separator and
            // the [updates] section.
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            lines.Add("");
            lines.Add("[updates]");
            lines.Add(newLine);
        }
        // Preserve a trailing newline if the original had one.
        var result = string.Join(newline, lines);
        if (modTxt.EndsWith("\n") && !result.EndsWith(newline))
            result += newline;
        return result;
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
