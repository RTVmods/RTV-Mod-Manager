// Save slots kept by the Save Slots mod (and, in the same layout, by Multi
// Save Slots and Multi Save Slots Reborn):
//
//   %APPDATA%\Road to Vostok\MultiSaveSlots\
//     state.cfg        [state] active_slot=<n>
//     pending.cfg      [pending] kind / slot / profile — read once by the mod
//                      on its next start, then deleted
//     slot<n>\         the save files of slot n, plus
//       _meta.cfg      [meta] display_name, last_map, last_play_iso, dead,
//                      difficulty, season, profile
//
// The manager reads the slots, edits two keys of a slot's _meta.cfg
// (display_name and profile: the profile link the mod also edits in-game),
// writes pending.cfg to launch the game straight into a slot, and zips the
// whole folder as a backup. It never copies save files in or out of a slot;
// that is the mod's job, done in-game where the game's own saves happen.
//
// The .cfg files are Godot ConfigFiles, written by the game. Edits keep every
// other line as it is.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace VostokModManager.Domain;

public static class SaveSlots
{
    public const string FolderName = "MultiSaveSlots";
    public const string MetaFile = "_meta.cfg";
    public const string StateFile = "state.cfg";
    public const string PendingFile = "pending.cfg";
    /// <summary>A slot holds a save when this file is in it, the same test
    /// the mod uses.</summary>
    public const string OccupancyMarker = "Character.tres";
    /// <summary>Slots the mod shows. More slot folders are listed too.</summary>
    public const int DefaultSlotCount = 5;

    /// <summary>Mod id of the companion mod.</summary>
    public const string CompanionModId = "save-slots";
    /// <summary>Mod ids of the other save-slot mods using the same folder;
    /// one of them live together with the companion means two mods syncing
    /// the same files.</summary>
    public static readonly string[] OtherSlotModIds = { "multi-save-slots", "multi-save-slots-reborn" };

    public record Slot(
        int Number,
        bool Occupied,
        string DisplayName,
        string LastMap,
        DateTime? LastPlayed,
        bool Dead,
        int Difficulty,
        int Season,
        string Profile)
    {
        public string Title => string.IsNullOrWhiteSpace(DisplayName) ? $"Slot {Number}" : DisplayName;
    }

    /// <summary>The slot folder beside the game's saves.</summary>
    public static string RootDir(string userDir) => Path.Combine(userDir, FolderName);

    /// <summary>The game's user folder, from the path of mod_config.cfg.</summary>
    public static string DefaultUserDir =>
        Path.GetDirectoryName(ModConfig.DefaultPath) ?? "";

    public static string SlotDir(string root, int n) => Path.Combine(root, "slot" + n);

    /// <summary>Every slot, 1..max(5, highest slot folder). Missing or
    /// unreadable metadata reads as empty fields.</summary>
    public static List<Slot> ReadAll(string root)
    {
        var count = DefaultSlotCount;
        if (Directory.Exists(root))
        {
            foreach (var d in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith("slot", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    && n > count && n < 100)
                    count = n;
            }
        }
        var list = new List<Slot>();
        for (var n = 1; n <= count; n++) list.Add(Read(root, n));
        return list;
    }

    public static Slot Read(string root, int n)
    {
        var dir = SlotDir(root, n);
        var occupied = File.Exists(Path.Combine(dir, OccupancyMarker));
        var meta = ReadSection(Path.Combine(dir, MetaFile), "meta");
        DateTime? played = null;
        if (meta.TryGetValue("last_play_iso", out var iso)
            && DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            played = t;
        return new Slot(
            n,
            occupied,
            meta.GetValueOrDefault("display_name", ""),
            meta.GetValueOrDefault("last_map", ""),
            played,
            string.Equals(meta.GetValueOrDefault("dead", ""), "true", StringComparison.OrdinalIgnoreCase),
            int.TryParse(meta.GetValueOrDefault("difficulty", ""), out var diff) ? diff : 0,
            int.TryParse(meta.GetValueOrDefault("season", ""), out var season) ? season : 0,
            meta.GetValueOrDefault("profile", ""));
    }

    /// <summary>The slot the mod last had active, 1 when unknown.</summary>
    public static int ActiveSlot(string root)
    {
        var state = ReadSection(Path.Combine(root, StateFile), "state");
        return int.TryParse(state.GetValueOrDefault("active_slot", ""), out var n) && n > 0 ? n : 1;
    }

    /// <summary>Ties slot n to a manager profile ("" = any profile).</summary>
    public static void SetProfile(string root, int n, string profile)
        => SetMetaValue(root, n, "profile", profile.Trim());

    public static void SetDisplayName(string root, int n, string name)
        => SetMetaValue(root, n, "display_name", name.Trim());

    /// <summary>Asks the mod to continue slot n on the game's next start.
    /// `profile` is what the slot expects to run under; the mod only uses
    /// it to log a mismatch.</summary>
    public static void WritePending(string root, int n, string profile, string kind = "load")
    {
        Directory.CreateDirectory(root);
        var text = new StringBuilder()
            .Append("[pending]\n\n")
            .Append("kind=").Append(Quote(kind)).Append('\n')
            .Append("slot=").Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("profile=").Append(Quote(profile)).Append('\n')
            .ToString();
        File.WriteAllText(Path.Combine(root, PendingFile), text, new UTF8Encoding(false));
    }

    /// <summary>Takes back a pending launch that did not happen.</summary>
    public static void ClearPending(string root)
    {
        try { File.Delete(Path.Combine(root, PendingFile)); } catch { }
    }

    /// <summary>Zips the whole slot folder to `zipPath`. Returns the
    /// number of files written.</summary>
    public static int Backup(string root, string zipPath)
    {
        if (!Directory.Exists(root)) return 0;
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var count = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            zip.CreateEntryFromFile(f, FolderName + "/" + rel, CompressionLevel.Optimal);
            count++;
        }
        return count;
    }

    // ── Godot ConfigFile, as much as these files use ──────────────────

    /// <summary>The key/value pairs of one section, values unquoted.
    /// Missing file or section = empty.</summary>
    public static Dictionary<string, string> ReadSection(string path, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return result; }
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = line[1..^1] == section;
                continue;
            }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = Unquote(line[(eq + 1)..].Trim());
        }
        return result;
    }

    /// <summary>Sets one [meta] key of slot n's _meta.cfg, keeping every
    /// other line. Creates the file (and slot folder) when missing.</summary>
    public static void SetMetaValue(string root, int n, string key, string value)
    {
        var dir = SlotDir(root, n);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, MetaFile);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var newLine = key + "=" + Quote(value);

        var sectionStart = lines.FindIndex(l => l.Trim() == "[meta]");
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[meta]");
            lines.Add("");
            lines.Add(newLine);
        }
        else
        {
            var end = lines.FindIndex(sectionStart + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var keyRe = new Regex("^\\s*" + Regex.Escape(key) + "\\s*=");
            var at = lines.FindIndex(sectionStart + 1, end - sectionStart - 1, l => keyRe.IsMatch(l));
            if (at >= 0)
            {
                lines[at] = newLine;
            }
            else
            {
                // After the section's last non-blank line.
                var insert = end;
                while (insert > sectionStart + 1 && lines[insert - 1].Trim().Length == 0) insert--;
                lines.Insert(insert, newLine);
            }
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    internal static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder(v.Length);
            for (var i = 1; i < v.Length - 1; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length - 1)
                {
                    i++;
                    sb.Append(v[i] switch { 'n' => '\n', 't' => '\t', _ => v[i] });
                }
                else sb.Append(v[i]);
            }
            return sb.ToString();
        }
        return v;
    }
}
