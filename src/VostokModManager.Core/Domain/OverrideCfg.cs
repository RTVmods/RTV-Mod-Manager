// Merges the Mod Loader's override.cfg template into the one in the game
// folder, the way MML's own installer does since 3.5.0: keys the template
// names take the template's value, everything else the user has ([display],
// [input], other autoloads, comments) stays, and a ModLoader line left under
// [autoload] by an install older than 3.x is dropped, because the loader now
// lives under [autoload_prepend] and must not be listed twice.

namespace VostokModManager.Domain;

public static class OverrideCfg
{
    /// <summary>The section the loader's autoload sits in; a merge or
    /// template without it would leave the game starting without mods.</summary>
    public const string LoaderSection = "[autoload_prepend]";

    public static bool LooksLikeLoaderTemplate(string text) => text.Contains(LoaderSection, StringComparison.Ordinal);

    public static string Merge(string user, string template)
    {
        var tmpl = ParseTemplate(template);
        var newline = user.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = user.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? section = null;
        var block = new List<string>();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (IsHeader(t, out var name))
            {
                Flush(section, block, tmpl, output);
                block.Clear();
                section = name;
                seen.Add(name);
            }
            block.Add(line);
        }
        Flush(section, block, tmpl, output);

        foreach (var (name, keys) in tmpl)
        {
            if (seen.Contains(name)) continue;
            if (output.Count > 0 && output[^1].Trim().Length > 0) output.Add("");
            output.Add("[" + name + "]");
            output.Add("");
            foreach (var (k, v) in keys) output.Add(k + "=" + v);
        }
        return string.Join(newline, output) + newline;
    }

    private static void Flush(string? section, List<string> block, List<(string Name, List<(string Key, string Value)> Keys)> tmpl,
        List<string> output)
    {
        var keys = section == null ? null : tmpl.FirstOrDefault(s => s.Name == section).Keys;
        var written = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var line in block)
        {
            var t = line.Trim();
            var eq = t.IndexOf('=');
            if (IsHeader(t, out _) || t.Length == 0 || t.StartsWith(';') || t.StartsWith('#') || eq < 0)
            {
                kept.Add(line);
                continue;
            }
            var key = t[..eq].Trim();
            if (section == "autoload" && key == "ModLoader") continue;
            var match = keys?.FirstOrDefault(k => k.Key == key);
            if (match is { Key: not null } m)
            {
                kept.Add(key + "=" + m.Value);
                written.Add(key);
            }
            else kept.Add(line);
        }
        // Keys the template adds go after the section's last key, not after its trailing blank lines.
        var at = kept.Count;
        while (at > 1 && kept[at - 1].Trim().Length == 0) at--;
        if (keys != null)
            kept.InsertRange(at, keys.Where(k => !written.Contains(k.Key)).Select(k => k.Key + "=" + k.Value));
        output.AddRange(kept);
    }

    private static List<(string Name, List<(string Key, string Value)> Keys)> ParseTemplate(string template)
    {
        var result = new List<(string Name, List<(string Key, string Value)> Keys)>();
        List<(string Key, string Value)>? current = null;
        foreach (var raw in template.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (IsHeader(t, out var name))
            {
                var existing = result.FindIndex(s => s.Name == name);
                if (existing >= 0) current = result[existing].Keys;
                else
                {
                    current = new List<(string Key, string Value)>();
                    result.Add((name, current));
                }
                continue;
            }
            if (current == null || t.Length == 0 || t.StartsWith(';') || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq < 0) continue;
            var key = t[..eq].Trim();
            var value = t[(eq + 1)..];
            var at = current.FindIndex(k => k.Key == key);
            if (at >= 0) current[at] = (key, value);
            else current.Add((key, value));
        }
        return result;
    }

    private static bool IsHeader(string trimmed, out string name)
    {
        name = "";
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']') return false;
        name = trimmed[1..^1];
        return true;
    }
}
