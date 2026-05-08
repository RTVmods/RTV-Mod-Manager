// Persisted user settings. JSON file under
//   %APPDATA%/VostokModManager/settings.json
// — chosen over the Registry because it's portable, transparent, and
// easy to delete to reset state.
//
// Static-ish API: Settings.Load() returns a populated instance (with
// defaults if the file's missing or unreadable); .Save() writes back.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VostokModManager;

public class Settings
{
    /// <summary>Override path to the claude CLI binary. Empty = let
    /// ClaudeCodeRunner auto-detect.</summary>
    public string ClaudePath { get; set; } = "";

    /// <summary>Path to the decompiled game source folder (Decomp/).
    /// Used as additional context when the AI resolver compares two
    /// mod overrides of the same game script. Optional.</summary>
    public string GameSourcePath { get; set; } = "";

    /// <summary>Override the default Road to Vostok mods folder.
    /// Empty = use the hardcoded Steam default.</summary>
    public string ModsDir { get; set; } = "";

    /// <summary>Snapshot of the most recent ModWorkshop /mods/versions
    /// response. Keyed by mod_workshop_id (as string for JSON
    /// compatibility), value is the latest version string.</summary>
    public Dictionary<string, string> CachedVersions { get; set; } = new();

    /// <summary>ISO-8601 (round-trip) timestamp of when CachedVersions
    /// was last refreshed. Empty when no check has run yet.</summary>
    public string CacheTimestamp { get; set; } = "";

    [JsonIgnore]
    public TimeSpan CacheAge
    {
        get
        {
            if (DateTime.TryParse(
                    CacheTimestamp, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var t))
                return DateTime.UtcNow - t;
            return TimeSpan.MaxValue;
        }
    }

    [JsonIgnore]
    public bool IsCacheFresh
        => CachedVersions.Count > 0 && CacheAge < TimeSpan.FromHours(1);

    public static string Path
    {
        get
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VostokModManager"
            );
            return System.IO.Path.Combine(dir, "settings.json");
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var s = JsonSerializer.Deserialize<Settings>(json, _opts);
                if (s != null) return s;
            }
        }
        catch
        {
            // First-run, corrupted file, perms issue — fall through to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, _opts);
        File.WriteAllText(Path, json);
    }

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
