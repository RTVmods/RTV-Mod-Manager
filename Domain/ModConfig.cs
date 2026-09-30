// Reads and writes %APPDATA%\Road to Vostok\mod_config.cfg, the mod
// loader's record of which mods are enabled, their load priorities and
// where each mod came from. Format (Godot ConfigFile):
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
//   [mod_sources]
//   mod-id@version="{\"id\":\"some-slug\",\"provider\":\"vostokmods\",\"version\":\"1.0\"}"
//
// The loader owns this file and the manager only edits parts of it, so
// the file is kept as raw text: every value is stored exactly as it
// appears on disk and written back verbatim unless the manager changed
// that one key. The values the manager does not manage include quoted
// strings (the [mod_sources] records are JSON inside a Godot string) and
// can span lines (dictionaries). Godot's parser rejects the whole file
// if any of them is re-written without its quotes, and the loader then
// treats mod_config.cfg as failed to load. An unedited load-then-save
// therefore reproduces the file byte for byte.
//
// The manager edits:
//   - [settings] active_profile
//   - [profile.<name>.enabled] and [profile.<name>.priority]
//   - [mod_sources] records for mods it links or installs

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VostokModManager.Domain;

public class ModConfig
{
    public const string ModSourcesSection = "mod_sources";
    private const string SettingsSection = "settings";
    private const string ActiveProfileKey = "active_profile";

    /// <summary>The active profile name from [settings] active_profile.
    /// Defaults to "Default" when the cfg is absent or has no value.</summary>
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Path the cfg was loaded from, used as the default
    /// save target.</summary>
    public string Path { get; private set; } = "";

    /// <summary>True if Load() found a real file on disk. False if
    /// we materialised an empty cfg because nothing was there yet
    /// (first-run or pristine install). Determines whether Save()
    /// rotates a .bak (no point backing up a non-existent file).</summary>
    public bool ExistsOnDisk { get; private set; }

    /// <summary>True when the file exists but could not be read (for
    /// example the game had it locked). Save() refuses in this state:
    /// writing the empty in-memory config over a file we never read
    /// would erase every profile.</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>One `key=value` line. RawValue is the text after the
    /// `=`, exactly as on disk (quotes, escapes and all). RawText is the
    /// entry's complete original text, which may span several lines;
    /// it is null once the manager has changed or created the entry.</summary>
    private sealed class Entry
    {
        public string Key = "";
        public string RawValue = "";
        public string? RawText;
    }

    private sealed class Section
    {
        public string Name = "";
        public readonly List<Entry> Entries = new();
        private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

        public Entry? Find(string key)
            => _byKey.TryGetValue(key, out var e) ? e : null;

        public void Put(Entry e)
        {
            if (_byKey.TryGetValue(e.Key, out var existing))
            {
                // A repeated key: the later line wins, in the earlier slot.
                Entries[Entries.IndexOf(existing)] = e;
            }
            else
            {
                Entries.Add(e);
            }
            _byKey[e.Key] = e;
        }

        public bool Remove(string key)
        {
            if (!_byKey.TryGetValue(key, out var e)) return false;
            _byKey.Remove(key);
            Entries.Remove(e);
            return true;
        }

        public void Clear()
        {
            _byKey.Clear();
            Entries.Clear();
        }
    }

    /// <summary>Sections in file order.</summary>
    private readonly List<Section> _sections = new();
    private readonly Dictionary<string, Section> _byName = new(StringComparer.Ordinal);

    /// <summary>The file's own line ending, reused on save.</summary>
    private string _newline = Environment.NewLine;

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
            c.Parse(File.ReadAllText(path));
        }
        catch
        {
            // Unreadable (locked, permissions). Leave the config empty
            // and flag it, so Save() cannot write that emptiness back.
            c._sections.Clear();
            c._byName.Clear();
            c.LoadFailed = true;
            return c;
        }
        var ap = c.GetString(SettingsSection, ActiveProfileKey);
        c.ActiveProfile = string.IsNullOrEmpty(ap) ? "Default" : ap;
        return c;
    }

    // ── enabled / priority ────────────────────────────────────────────

    /// <summary>Returns the enabled state for `modId@version`. Falls
    /// back to `fallback` (default true) when the cfg has no entry —
    /// fresh installs of a new mod default to enabled, matching the
    /// in-game loader's behaviour.</summary>
    public bool IsEnabled(string modId, string version, bool fallback = true)
    {
        if (string.IsNullOrEmpty(modId)) return fallback;
        var raw = GetRaw($"profile.{ActiveProfile}.enabled", MakeKey(modId, version));
        if (raw == null) return fallback;
        return string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the priority for `modId@version`. Falls back
    /// when the cfg has no entry — caller typically passes the
    /// mod.txt-declared priority so cfg-overridden mods use cfg and
    /// untouched mods use their author-declared default.</summary>
    public int Priority(string modId, string version, int fallback = 0)
    {
        if (string.IsNullOrEmpty(modId)) return fallback;
        var raw = GetRaw($"profile.{ActiveProfile}.priority", MakeKey(modId, version));
        if (raw != null && int.TryParse(raw.Trim(), out var p)) return p;
        return fallback;
    }

    public void SetEnabled(string modId, string version, bool value)
        => SetRaw(
            $"profile.{ActiveProfile}.enabled",
            MakeKey(modId, version),
            value ? "true" : "false");

    public void SetPriority(string modId, string version, int value)
        => SetRaw(
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
        return GetRaw($"profile.{ActiveProfile}.enabled", key) != null
            || GetRaw($"profile.{ActiveProfile}.priority", key) != null;
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
        if (_byName.TryGetValue($"profile.{ActiveProfile}.enabled", out var en))
            en.Remove(key);
        if (_byName.TryGetValue($"profile.{ActiveProfile}.priority", out var pr))
            pr.Remove(key);
    }

    /// <summary>Wipes both `[profile.<name>.enabled]` and
    /// `[profile.<name>.priority]` sections for the given profile.
    /// Used by ProfileSwitcher to make cfg deterministic from
    /// profile.json — clear the section, then re-emit each
    /// ProfileMod's state. Other profiles' sections are NOT touched,
    /// and unrelated sections (e.g. `settings`) are preserved.</summary>
    public void ClearProfileEntries(string profileName)
    {
        if (string.IsNullOrEmpty(profileName)) return;
        if (_byName.TryGetValue($"profile.{profileName}.enabled", out var en))
            en.Clear();
        if (_byName.TryGetValue($"profile.{profileName}.priority", out var pr))
            pr.Clear();
    }

    // ── mod sources ───────────────────────────────────────────────────

    /// <summary>Where the loader (or the manager) recorded this mod as
    /// coming from, read from `[mod_sources]`. Returns
    /// <see cref="ModSource.None"/> when there is no record, or the
    /// record names a host the manager does not use.</summary>
    public ModSource GetModSource(string modId, string version)
    {
        if (string.IsNullOrEmpty(modId)) return ModSource.None;
        var raw = GetRaw(ModSourcesSection, MakeKey(modId, version));
        if (raw == null) return ModSource.None;
        try
        {
            using var doc = JsonDocument.Parse(Unquote(raw));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ModSource.None;
            var provider = root.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? "" : "";
            var id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString() ?? "" : "";
            return ModSource.Parse(provider + ":" + id);
        }
        catch (JsonException)
        {
            return ModSource.None;
        }
    }

    /// <summary>Records where a mod came from in `[mod_sources]`, in
    /// the loader's own record shape. An invalid source removes the
    /// record.</summary>
    public void SetModSource(string modId, string version, ModSource source)
    {
        if (string.IsNullOrEmpty(modId)) return;
        if (!source.IsValid)
        {
            RemoveModSource(modId, version);
            return;
        }
        // Keys in the loader's order (alphabetical), so its "has this
        // record changed" string comparison sees no difference.
        var json = "{\"id\":" + JsonString(source.Id)
            + ",\"provider\":" + JsonString(source.Provider)
            + (string.IsNullOrEmpty(version) ? "" : ",\"version\":" + JsonString(version))
            + "}";
        SetRaw(ModSourcesSection, MakeKey(modId, version), Quote(json));
    }

    public void RemoveModSource(string modId, string version)
    {
        if (string.IsNullOrEmpty(modId)) return;
        if (_byName.TryGetValue(ModSourcesSection, out var sec))
            sec.Remove(MakeKey(modId, version));
    }

    // ── save ──────────────────────────────────────────────────────────

    /// <summary>Writes the cfg back to disk. Rotates the previous
    /// file into .bak.1 (and existing .bak.N → .bak.N+1, up to 10).
    /// Throws if the file could not be read when it was loaded, if the
    /// parent directory can't be created or if the file can't be
    /// written — caller decides whether to surface that to the user or
    /// swallow it.</summary>
    public void Save(string? path = null)
    {
        path ??= Path;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("Path not set; call Load(path) first or pass an explicit path.");
        if (LoadFailed)
            throw new InvalidOperationException(
                "mod_config.cfg could not be read when it was loaded, so it is not being overwritten. "
                + "Close Road to Vostok if it is running, then Refresh.");

        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        SyncActiveProfile();
        var text = Serialize();

        if (File.Exists(path)) RotateBackups(path, maxKeep: 10);

        // Write beside the target, then swap, so a failure part-way
        // through never leaves a truncated config.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    private void SyncActiveProfile()
    {
        if (!_byName.TryGetValue(SettingsSection, out var settings))
        {
            settings = new Section { Name = SettingsSection };
            _sections.Insert(0, settings);
            _byName[SettingsSection] = settings;
        }
        var existing = settings.Find(ActiveProfileKey);
        if (existing == null)
            settings.Put(new Entry { Key = ActiveProfileKey, RawValue = Quote(ActiveProfile) });
        else if (Unquote(existing.RawValue) != ActiveProfile)
        {
            existing.RawValue = Quote(ActiveProfile);
            existing.RawText = null;
        }
    }

    /// <summary>Godot's layout: a header, a blank line, the section's
    /// keys, and a blank line between sections (none after the last).</summary>
    private string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var section in _sections)
        {
            if (sb.Length > 0) sb.Append(_newline);
            sb.Append('[').Append(section.Name).Append(']').Append(_newline);
            sb.Append(_newline);
            foreach (var e in section.Entries)
            {
                if (e.RawText != null)
                    sb.Append(e.RawText.Replace("\n", _newline));
                else
                    sb.Append(FormatKey(e.Key)).Append('=').Append(e.RawValue);
                sb.Append(_newline);
            }
        }
        return sb.ToString();
    }

    // ── raw access ────────────────────────────────────────────────────

    private string? GetRaw(string section, string key)
        => _byName.TryGetValue(section, out var sec) ? sec.Find(key)?.RawValue : null;

    /// <summary>A value with its Godot string quoting removed, or ""
    /// when the key is absent.</summary>
    private string GetString(string section, string key)
    {
        var raw = GetRaw(section, key);
        return raw == null ? "" : Unquote(raw);
    }

    private void SetRaw(string section, string key, string rawValue)
    {
        if (!_byName.TryGetValue(section, out var sec))
        {
            sec = new Section { Name = section };
            _sections.Add(sec);
            _byName[section] = sec;
        }
        var existing = sec.Find(key);
        if (existing == null)
        {
            sec.Put(new Entry { Key = key, RawValue = rawValue });
            return;
        }
        // Leave an unchanged value's original text alone.
        if (string.Equals(existing.RawValue.Trim(), rawValue, StringComparison.Ordinal)) return;
        existing.RawValue = rawValue;
        existing.RawText = null;
    }

    // ── parsing ───────────────────────────────────────────────────────

    private void Parse(string text)
    {
        _sections.Clear();
        _byName.Clear();
        if (text.Contains("\r\n")) _newline = "\r\n";
        else if (text.Contains('\n')) _newline = "\n";

        var lines = text.Replace("\r\n", "\n").Split('\n');
        Section? current = null;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#')
            {
                i++;
                continue;
            }
            if (t[0] == '[' && t[^1] == ']')
            {
                var name = t.Substring(1, t.Length - 2);
                if (!_byName.TryGetValue(name, out current))
                {
                    current = new Section { Name = name };
                    _sections.Add(current);
                    _byName[name] = current;
                }
                i++;
                continue;
            }
            if (current == null || !TrySplitKey(line, out var key, out var valueStart))
            {
                i++;
                continue;
            }

            // A value runs on to following lines while a string or a
            // bracket opened in it is still open.
            var rawText = new StringBuilder(line);
            var value = new StringBuilder(line, valueStart, line.Length - valueStart, line.Length);
            var inString = false;
            var escaped = false;
            var depth = 0;
            ScanValue(line, valueStart, ref inString, ref escaped, ref depth);
            while ((inString || depth > 0) && i + 1 < lines.Length)
            {
                i++;
                rawText.Append('\n').Append(lines[i]);
                value.Append('\n').Append(lines[i]);
                // A line break inside a string is part of the string.
                escaped = false;
                ScanValue(lines[i], 0, ref inString, ref escaped, ref depth);
            }

            current.Put(new Entry
            {
                Key = key,
                RawValue = value.ToString().Trim(),
                RawText = rawText.ToString(),
            });
            i++;
        }
    }

    /// <summary>Splits `key=value`. The key may be a quoted string
    /// (which can itself contain `=`). Returns the unquoted key and the
    /// index where the value starts.</summary>
    private static bool TrySplitKey(string line, out string key, out int valueStart)
    {
        key = "";
        valueStart = 0;
        var p = 0;
        while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
        if (p >= line.Length) return false;

        if (line[p] == '"')
        {
            var end = p + 1;
            var esc = false;
            while (end < line.Length)
            {
                var ch = line[end];
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') break;
                end++;
            }
            if (end >= line.Length) return false;
            var eq = end + 1;
            while (eq < line.Length && char.IsWhiteSpace(line[eq])) eq++;
            if (eq >= line.Length || line[eq] != '=') return false;
            key = Unescape(line.Substring(p + 1, end - p - 1));
            valueStart = eq + 1;
            return key.Length > 0;
        }

        var idx = line.IndexOf('=', p);
        if (idx <= p) return false;
        key = line.Substring(p, idx - p).Trim();
        valueStart = idx + 1;
        return key.Length > 0;
    }

    private static void ScanValue(string s, int start, ref bool inString, ref bool escaped, ref int depth)
    {
        for (var i = start; i < s.Length; i++)
        {
            var ch = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
            }
            else if (ch == '"') inString = true;
            else if (ch == '{' || ch == '[' || ch == '(') depth++;
            else if ((ch == '}' || ch == ']' || ch == ')') && depth > 0) depth--;
        }
    }

    // ── Godot string encoding ─────────────────────────────────────────

    /// <summary>A Godot string literal: quoted, with `\` and `"` escaped.</summary>
    private static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The text of a Godot string literal. A value that is not
    /// a quoted string is returned trimmed and otherwise unchanged.</summary>
    private static string Unquote(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            return Unescape(t.Substring(1, t.Length - 2));
        return t;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch != '\\' || i + 1 >= s.Length)
            {
                sb.Append(ch);
                continue;
            }
            var next = s[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'a': sb.Append('\a'); break;
                case 'v': sb.Append('\v'); break;
                case 'u' when i + 4 < s.Length
                    && int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var u4):
                    sb.Append((char)u4);
                    i += 4;
                    break;
                case 'U' when i + 6 < s.Length
                    && int.TryParse(s.AsSpan(i + 1, 6), System.Globalization.NumberStyles.HexNumber, null, out var u6)
                    && u6 <= 0x10FFFF && (u6 < 0xD800 || u6 > 0xDFFF):
                    sb.Append(char.ConvertFromUtf32(u6));
                    i += 6;
                    break;
                default:
                    // `\\`, `\"`, `\'` and anything unrecognised: the
                    // character itself.
                    sb.Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions _jsonStringOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string JsonString(string s) => JsonSerializer.Serialize(s, _jsonStringOpts);

    /// <summary>Quotes a key the way Godot does: only when it contains
    /// a character its parser treats specially, a space or control
    /// character, or anything outside printable ASCII.</summary>
    private static string FormatKey(string key)
    {
        var plain = key.Length > 0;
        foreach (var c in key)
        {
            if (c == '=' || c == '"' || c == ';' || c == '[' || c == ']' || c < 33 || c > 126)
            {
                plain = false;
                break;
            }
        }
        return plain ? key : Quote(key);
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
