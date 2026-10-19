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

    /// <summary>Tells the mod what to do on the game's next start:
    /// kind "load" continues slot n; kind "new" opens the mode screen and
    /// starts the new run in slot n, named `name`. `profile` is what the
    /// slot expects to run under; the mod only uses it to log a mismatch.</summary>
    public static void WritePending(string root, int n, string profile, string kind = "load", string name = "")
    {
        Directory.CreateDirectory(root);
        var text = new StringBuilder()
            .Append("[pending]\n\n")
            .Append("kind=").Append(Quote(kind)).Append('\n')
            .Append("slot=").Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("profile=").Append(Quote(profile)).Append('\n')
            .Append("name=").Append(Quote(name)).Append('\n')
            .ToString();
        File.WriteAllText(Path.Combine(root, PendingFile), text, new UTF8Encoding(false));
    }

    /// <summary>True while Road to Vostok is running. Slot files must not
    /// change under it: it writes the save on quit.</summary>
    public static bool GameRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("RTV").Length > 0; }
        catch { return false; }
    }

    // ── which files in the game folder belong to a save ───────────────

    /// <summary>The mod's file rules (MultiSaveSlots\file_rules.cfg, with
    /// the mod's defaults for anything missing): which files in the
    /// game's save folder are part of a slot. Same evaluation order as
    /// the mod.</summary>
    public class FileRules
    {
        public List<string> TrackedExtensions { get; init; } = new() { "tres", "json", "save", "jpg" };
        public List<string> NeverTouchFiles { get; init; } = new()
        {
            "modloader.gd", "mod_config.cfg", "mod_profiles.cfg", "modloader_conflicts.txt",
            "override.cfg", "Validator.tres", "Preferences.tres", "simplehud_preferences.json",
        };
        public List<string> NeverTouchExtensions { get; init; } = new() { "gd", "txt", "cfg", "ini" };
        public List<string> ForceTrackedFiles { get; init; } = new() { "XPData.cfg", "XPPrestige.cfg", "XPSkillsBookCache.cfg" };
        public List<string> ForceTrackedPrefixes { get; init; } = new() { "XPData_" };
        public List<string> NeverTouchSuffixes { get; init; } = new() { "_preferences.json", "_settings.json", "_config.json", "_prefs.json" };

        public static FileRules Load(string root)
        {
            var d = new FileRules();
            var s = ReadSection(Path.Combine(root, "file_rules.cfg"), "file_rules");
            List<string> Get(string key, List<string> fallback)
                => s.TryGetValue(key, out var raw) && TryParseList(raw, out var list) ? list : fallback;
            return new FileRules
            {
                TrackedExtensions = Get("tracked_file_extensions", d.TrackedExtensions),
                NeverTouchFiles = Get("never_touch_files", d.NeverTouchFiles),
                NeverTouchExtensions = Get("never_touch_extensions", d.NeverTouchExtensions),
                ForceTrackedFiles = Get("force_tracked_files", d.ForceTrackedFiles),
                ForceTrackedPrefixes = Get("force_tracked_prefixes", d.ForceTrackedPrefixes),
                NeverTouchSuffixes = Get("never_touch_suffixes", d.NeverTouchSuffixes),
            };
        }

        private static string Ext(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
        }

        private bool ForceTracked(string name)
            => ForceTrackedFiles.Contains(name) || ForceTrackedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

        private bool Blacklisted(string name)
            => NeverTouchFiles.Contains(name)
                || NeverTouchExtensions.Contains(Ext(name))
                || NeverTouchSuffixes.Any(sfx => name.ToLowerInvariant().EndsWith(sfx, StringComparison.Ordinal));

        /// <summary>The mod's _is_trackable_file and _is_safe_to_touch
        /// together: a file the mod moves in and out of slots.</summary>
        public bool IsSaveFile(string name)
        {
            if (NeverTouchFiles.Contains(name)) return false;
            if (ForceTracked(name)) return true;
            if (Blacklisted(name)) return false;
            return TrackedExtensions.Contains(Ext(name));
        }
    }

    /// <summary>Parses a Godot array of strings: ["a", "b"].</summary>
    internal static bool TryParseList(string raw, out List<string> list)
    {
        list = new List<string>();
        raw = raw.Trim();
        if (raw.Length < 2 || raw[0] != '[' || raw[^1] != ']') return false;
        foreach (Match m in Regex.Matches(raw, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            list.Add(Unquote("\"" + m.Groups[1].Value + "\""));
        return true;
    }

    /// <summary>The save files of the slot in play, in the game's save folder.</summary>
    public static List<string> LiveSaveFiles(string userDir, FileRules rules)
        => Directory.Exists(userDir)
            ? Directory.GetFiles(userDir).Where(f => rules.IsSaveFile(Path.GetFileName(f))).ToList()
            : new List<string>();

    /// <summary>Deletes slot n: zips its files to `backupZip` first (and,
    /// for the slot in play, its files in the game's save folder too),
    /// then removes them. The slot in play has to go from the game folder
    /// as well, or the mod's crash recovery copies it straight back into
    /// the slot on the next start. Returns the number of files removed.</summary>
    public static int DeleteSlot(string root, string userDir, int n, string backupZip)
    {
        var dir = SlotDir(root, n);
        var slotFiles = Directory.Exists(dir) ? Directory.GetFiles(dir).ToList() : new List<string>();
        var live = n == ActiveSlot(root) ? LiveSaveFiles(userDir, FileRules.Load(root)) : new List<string>();
        if (slotFiles.Count == 0 && live.Count == 0) return 0;

        var bdir = Path.GetDirectoryName(backupZip);
        if (!string.IsNullOrEmpty(bdir)) Directory.CreateDirectory(bdir);
        if (File.Exists(backupZip)) File.Delete(backupZip);
        using (var zip = ZipFile.Open(backupZip, ZipArchiveMode.Create))
        {
            foreach (var f in slotFiles)
                zip.CreateEntryFromFile(f, $"{FolderName}/slot{n}/{Path.GetFileName(f)}", CompressionLevel.Optimal);
            foreach (var f in live)
                zip.CreateEntryFromFile(f, $"game-folder/{Path.GetFileName(f)}", CompressionLevel.Optimal);
        }
        var removed = 0;
        foreach (var f in slotFiles.Concat(live)) { File.Delete(f); removed++; }
        return removed;
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
