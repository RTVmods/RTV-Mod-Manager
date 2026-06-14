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

    /// <summary>Latest version string published for the mod manager
    /// itself on ModWorkshop (modid 56801). Empty when no check has
    /// run yet. We use the same /mods/versions endpoint we use for
    /// every other tracked mod — the manager just isn't a mod in the
    /// mods grid, so it gets its own status row + cache slot.</summary>
    public string ManagerLatestVersion { get; set; } = "";

    /// <summary>ISO-8601 timestamp of the last successful manager
    /// version check. We re-poll once per 24h, same TTL as MML.</summary>
    public string ManagerCheckedAt { get; set; } = "";

    [JsonIgnore]
    public bool IsManagerCacheFresh
    {
        get
        {
            if (string.IsNullOrEmpty(ManagerLatestVersion)) return false;
            if (!DateTime.TryParse(
                    ManagerCheckedAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var t)) return false;
            return (DateTime.UtcNow - t) < TimeSpan.FromHours(24);
        }
    }

    /// <summary>Latest MML (Vostok Mod Loader) release tag fetched
    /// from GitHub's releases/latest endpoint. Empty when no check
    /// has run yet.</summary>
    public string MmlLatestTag { get; set; } = "";

    /// <summary>HTML page URL for the latest MML release (clickable
    /// in the status row).</summary>
    public string MmlLatestUrl { get; set; } = "";

    /// <summary>ISO-8601 timestamp of the last successful MML
    /// version check. We re-poll once per 24h.</summary>
    public string MmlCheckedAt { get; set; } = "";

    [JsonIgnore]
    public bool IsMmlCacheFresh
    {
        get
        {
            if (string.IsNullOrEmpty(MmlLatestTag)) return false;
            if (!DateTime.TryParse(
                    MmlCheckedAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var t)) return false;
            return (DateTime.UtcNow - t) < TimeSpan.FromHours(24);
        }
    }

    /// <summary>Snapshot of mod descriptions fetched from
    /// /mods/&lt;id&gt;. Keyed by mod_workshop_id (as string for JSON
    /// compatibility), value is the description text. Populated
    /// lazily — only mods whose description has been viewed (or
    /// proactively prefetched) end up here. Refreshed independently
    /// of CachedVersions; no TTL beyond "user clicked refresh".</summary>
    public Dictionary<string, string> CachedDescriptions { get; set; } = new();

    /// <summary>Last-known window bounds and state, restored on
    /// startup. 0/0/0/0 = "no saved state, use the form's coded
    /// defaults". Position is validated against current screens at
    /// load time so a multi-monitor change doesn't park us offscreen.
    /// WindowMaximized: when true, ignore Width/Height/Left/Top and
    /// just maximize on the screen the saved bounds intersect.</summary>
    public int WindowLeft { get; set; }
    public int WindowTop { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Persisted bounds of the embedded ModWorkshop browser
    /// window, so it reopens at the size/position the user last left it.
    /// Zero width/height = "use the default" (first run). Validated
    /// against current screens at open time, same as the main window.</summary>
    public int BrowserLeft { get; set; }
    public int BrowserTop { get; set; }
    public int BrowserWidth { get; set; }
    public int BrowserHeight { get; set; }
    public bool BrowserMaximized { get; set; }

    /// <summary>Splitter ratio between the mods grid (Panel1) and the
    /// conflicts grid (Panel2) — 0 means "no saved value, use the
    /// 0.62 default". Stored as a fraction rather than absolute
    /// pixels so the ratio is preserved when the window resizes.</summary>
    public double SplitterRatio { get; set; }

    /// <summary>Name of the currently-active mod profile, or empty
    /// before the first profile has been created. The manager keeps
    /// this in sync with the game's mod_config.cfg
    /// `[settings] active_profile` — switching the dropdown rewrites
    /// both. An empty string means the new profile model hasn't been
    /// adopted yet on this install (pre-migration); ModRegistry's
    /// existing pass + grid behave the way they did before
    /// migration was offered.</summary>
    public string ActiveProfileName { get; set; } = "";

    /// <summary>True after the user has explicitly skipped the
    /// migration prompt. Without this, the prompt would re-appear
    /// on every launch — annoying for users who want to keep the
    /// legacy "all mods, no profile" workflow. Reset to false on
    /// any successful migration.</summary>
    public bool MigrationDeclined { get; set; }

    /// <summary>Whether the right-hand Conflicts sidebar is shown.
    /// Default true — first-run users get the full split layout. When
    /// false, the SplitContainer's Panel2 is collapsed and the mods
    /// grid expands to fill the entire width. Toggled by the
    /// "Hide/Show conflicts" button on the mods toolbar; the saved
    /// SplitterRatio is preserved so re-showing restores the same
    /// proportions.</summary>
    public bool ConflictsVisible { get; set; } = true;

    /// <summary>Mod IDs the user has explicitly locked. Locked mods
    /// are skipped by Enable all / Disable all bulk toggles — useful
    /// when a single mod (e.g. Mod Configuration Menu) should always
    /// stay in its current state regardless of mass actions.
    /// Stored as a list rather than a set for JSON compatibility;
    /// callers should treat it as a set.</summary>
    public List<string> LockedMods { get; set; } = new();

    /// <summary>Mod IDs flagged "Testing Mod" — purely a visual
    /// marker: rows render with a yellow background so the user
    /// can spot which mods they're currently shaking out at a
    /// glance, without disturbing enable / lock / priority state.
    /// Toggle via the row's right-click menu. Persists across
    /// sessions like LockedMods.</summary>
    public List<string> TestingMods { get; set; } = new();

    /// <summary>Per-column widths the user dragged to. Keyed by
    /// "&lt;grid&gt;.&lt;column-name&gt;" — e.g. "mods.Update",
    /// "conflicts.Type". Fill-mode columns (the Mod-name column,
    /// for instance) are intentionally NOT persisted here because
    /// they re-compute their own width from the leftover space and
    /// pinning them would break that behaviour on window resize.</summary>
    public Dictionary<string, int> ColumnWidths { get; set; } = new();

    /// <summary>Free-form per-mod notes — keyed by mod_id (case-
    /// preserved in the dict but the manager reads case-insensitively).
    /// Surfaced as a tooltip on the Mod-name cell and edited via the
    /// row's right-click menu. Use cases: remembering why a particular
    /// mod is locked, tracking a personal "swap when v2 ships" plan,
    /// jotting compatibility notes that the in-game loader doesn't
    /// know about. Persisted in settings.json so notes survive
    /// uninstall / reinstall.</summary>
    public Dictionary<string, string> ModNotes { get; set; } = new();

    /// <summary>Names of pack groups the user has collapsed in
    /// the mods grid. Persisted so the collapsed/expanded state
    /// survives between sessions — collapsing the "Big audio
    /// pack" group should still hide its 30 mods the next time
    /// the manager opens. Case-insensitive lookups via the
    /// IsPackCollapsed helper below.</summary>
    public List<string> CollapsedPacks { get; set; } = new();

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
