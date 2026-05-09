// Reads and writes %APPDATA%\Road to Vostok\mod_config.cfg — the
// in-game mod loader's source of truth for which mods are enabled
// and their load priorities. Format:
//
//   [settings]
//   developer_mode=false
//   active_profile="Default"
//
//   [profile.Default.enabled]
//   mod-id@version=true
//   "Quoted Name@version"=true
//
//   [profile.Default.priority]
//   mod-id@version=10
//
// We use ModArchive.ParseConfigFile for the read path (same INI-ish
// parser as mod.txt — Godot ConfigFile syntax) and a hand-rolled
// writer for the save path.
//
// We preserve unknown sections + keys verbatim so a future loader
// version that adds new fields doesn't lose data when we re-write.
// Other profiles' [profile.Other.enabled] etc. blocks are also
// preserved untouched — we only edit the active profile's blocks.

using System.Text;

namespace VostokModManager.Domain;

public class ModConfig
{
    /// <summary>The active profile name from [settings] active_profile.
    /// Defaults to "Default" when the cfg is absent or malformed.</summary>
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Path the cfg was loaded from, used as the default
    /// save target.</summary>
    public string Path { get; private set; } = "";

    /// <summary>True if Load() found a real file on disk. False if
    /// we materialised an empty cfg because nothing was there yet
    /// (first-run or pristine install). Determines whether Save()
    /// rotates a .bak (no point backing up a non-existent file).</summary>
    public bool ExistsOnDisk { get; private set; }

    /// <summary>section → { key → raw value (string) }. Keys are
    /// stored without their surrounding quotes so lookups like
    /// `keys["Immersive Door Sounds@1.0.1"]` work regardless of
    /// how they were written. Re-quoting happens at save time
    /// based on whether the key contains chars that Godot would
    /// otherwise interpret.</summary>
    private Dictionary<string, Dictionary<string, string>> _sections = new();

    /// <summary>Default location for the cfg file (per the in-game
    /// loader). Won't be valid if APPDATA isn't resolvable.</summary>
    public static string DefaultPath
    {
        get
        {
            var appdata = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appdata)
                ? ""
                : System.IO.Path.Combine(appdata, "Road to Vostok", "mod_config.cfg");
        }
    }

    public static ModConfig Load(string path)
    {
        var c = new ModConfig { Path = path };
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return c;
        c.ExistsOnDisk = true;
        try
        {
            var text = File.ReadAllText(path);
            c._sections = ModArchive.ParseConfigFile(text);
            // active_profile is stored as a quoted Variant string —
            // ParseConfigFile already strips outer quotes so we get
            // the bare name here.
            if (c._sections.TryGetValue("settings", out var s)
                && s.TryGetValue("active_profile", out var ap))
                c.ActiveProfile = string.IsNullOrEmpty(ap) ? "Default" : ap;
        }
        catch
        {
            // Corrupt cfg: start empty so we don't propagate garbage
            // back when the user toggles something. Save() will
            // overwrite (and the .bak rotation preserves the
            // pre-corruption file if it exists).
            c._sections = new();
        }
        return c;
    }

    /// <summary>Returns the enabled state for `modId@version`. Falls
    /// back to `fallback` (default true) when the cfg has no entry —
    /// fresh installs of a new mod default to enabled, matching the
    /// in-game loader's behaviour.</summary>
    public bool IsEnabled(string modId, string version, bool fallback = true)
    {
        if (string.IsNullOrEmpty(modId)) return fallback;
        var key = MakeKey(modId, version);
        var section = $"profile.{ActiveProfile}.enabled";
        if (_sections.TryGetValue(section, out var sec)
            && sec.TryGetValue(key, out var v))
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        return fallback;
    }

    /// <summary>Returns the priority for `modId@version`. Falls back
    /// when the cfg has no entry — caller typically passes the
    /// mod.txt-declared priority so cfg-overridden mods use cfg and
    /// untouched mods use their author-declared default.</summary>
    public int Priority(string modId, string version, int fallback = 0)
    {
        if (string.IsNullOrEmpty(modId)) return fallback;
        var key = MakeKey(modId, version);
        var section = $"profile.{ActiveProfile}.priority";
        if (_sections.TryGetValue(section, out var sec)
            && sec.TryGetValue(key, out var v)
            && int.TryParse(v, out var p))
            return p;
        return fallback;
    }

    public void SetEnabled(string modId, string version, bool value)
        => SetSectionKey(
            $"profile.{ActiveProfile}.enabled",
            MakeKey(modId, version),
            value ? "true" : "false");

    public void SetPriority(string modId, string version, int value)
        => SetSectionKey(
            $"profile.{ActiveProfile}.priority",
            MakeKey(modId, version),
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>True iff the cfg has an explicit entry (enabled or
    /// priority) for this mod under the active profile. Useful for
    /// the "is this cfg-managed yet?" question — newly-installed
    /// mods that the in-game loader hasn't touched yet would return
    /// false here.</summary>
    public bool HasEntry(string modId, string version)
    {
        if (string.IsNullOrEmpty(modId)) return false;
        var key = MakeKey(modId, version);
        return SectionContains($"profile.{ActiveProfile}.enabled", key)
            || SectionContains($"profile.{ActiveProfile}.priority", key);
    }

    /// <summary>Drops both the enabled and priority entries for a
    /// `modId@version` from the active profile. Used by the update
    /// flow to clean up stale keys after a version bump — the
    /// installed mod is now at @newVersion, and @oldVersion's
    /// cfg state is dead weight nobody can address anymore.</summary>
    public void RemoveEntry(string modId, string version)
    {
        if (string.IsNullOrEmpty(modId)) return;
        var key = MakeKey(modId, version);
        if (_sections.TryGetValue($"profile.{ActiveProfile}.enabled", out var en))
            en.Remove(key);
        if (_sections.TryGetValue($"profile.{ActiveProfile}.priority", out var pr))
            pr.Remove(key);
    }

    private bool SectionContains(string section, string key)
        => _sections.TryGetValue(section, out var sec) && sec.ContainsKey(key);

    private void SetSectionKey(string section, string key, string value)
    {
        if (!_sections.TryGetValue(section, out var sec))
        {
            sec = new Dictionary<string, string>();
            _sections[section] = sec;
        }
        sec[key] = value;
    }

    /// <summary>Writes the cfg back to disk. Rotates the previous
    /// file into .bak.1 (and existing .bak.N → .bak.N+1, up to 10).
    /// Throws if the parent directory can't be created or the file
    /// can't be written — caller decides whether to surface that
    /// to the user or swallow it.</summary>
    public void Save(string? path = null)
    {
        path ??= Path;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("Path not set; call Load(path) first or pass an explicit path.");

        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(path)) RotateBackups(path, maxKeep: 10);

        var sb = new StringBuilder();

        // [settings] always first; preserve any extra keys (developer_mode etc.)
        sb.AppendLine("[settings]");
        sb.AppendLine();
        var settings = _sections.TryGetValue("settings", out var sset)
            ? sset
            : new Dictionary<string, string>();
        // active_profile is a Variant string → emit quoted.
        var apWritten = false;
        foreach (var (k, v) in settings)
        {
            if (k == "active_profile")
            {
                sb.AppendLine($"active_profile=\"{ActiveProfile}\"");
                apWritten = true;
            }
            else
            {
                sb.AppendLine($"{FormatKey(k)}={v}");
            }
        }
        if (!apWritten) sb.AppendLine($"active_profile=\"{ActiveProfile}\"");
        sb.AppendLine();

        // Active profile's enabled + priority next (the user-visible
        // ones), then any other sections we don't manage but want
        // to preserve.
        var activeEnabled = $"profile.{ActiveProfile}.enabled";
        var activePriority = $"profile.{ActiveProfile}.priority";
        WriteSection(sb, activeEnabled);
        WriteSection(sb, activePriority);

        foreach (var (section, _) in _sections)
        {
            if (section == "settings"
                || section == activeEnabled
                || section == activePriority)
                continue;
            WriteSection(sb, section);
        }

        File.WriteAllText(path, sb.ToString());
    }

    private void WriteSection(StringBuilder sb, string section)
    {
        if (!_sections.TryGetValue(section, out var sec)) return;
        sb.AppendLine($"[{section}]");
        sb.AppendLine();
        foreach (var (k, v) in sec)
            sb.AppendLine($"{FormatKey(k)}={v}");
        sb.AppendLine();
    }

    /// <summary>Quotes a key when it contains chars Godot's parser
    /// would otherwise treat specially (whitespace, & etc.). Plain
    /// alphanumeric + .-_@ keys are written bare for readability.</summary>
    private static string FormatKey(string key)
    {
        bool plain = key.Length > 0
            && key.All(c =>
                char.IsLetterOrDigit(c)
                || c == '_' || c == '-' || c == '.' || c == '@');
        return plain ? key : $"\"{key}\"";
    }

    private static void RotateBackups(string path, int maxKeep)
    {
        // .bak.maxKeep gets discarded; .bak.(N) → .bak.(N+1) for
        // N from maxKeep-1 down to 1; current file → .bak.1.
        try
        {
            var oldest = $"{path}.bak.{maxKeep}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var n = maxKeep - 1; n >= 1; n--)
            {
                var src = $"{path}.bak.{n}";
                if (File.Exists(src)) File.Move(src, $"{path}.bak.{n + 1}", overwrite: true);
            }
            File.Copy(path, $"{path}.bak.1", overwrite: false);
        }
        catch
        {
            // Backup-rotation failures aren't worth blocking the
            // save — the user's edits matter more than the .bak
            // chain. The save path itself is the canonical state.
        }
    }

    private static string MakeKey(string modId, string version)
        => string.IsNullOrEmpty(version) ? modId : $"{modId}@{version}";
}
