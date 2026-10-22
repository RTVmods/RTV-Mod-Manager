// Plain data the Mods area (ManagerSession.Mods.cs) hands to the front
// end: the mods list rows, the status counts, install and drop results,
// and the load-order changes a drag would make.

using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>What the mods list should show. `Filter` is a substring
/// matched case-insensitively against the display name, mod id and file
/// name. `DependencyFilter`, when set, narrows the list to those mod ids
/// (the WinForms front end set it while the Dependencies dialog was
/// open); locked mods always stay visible.</summary>
public sealed record ModListQuery(string Filter = "", IReadOnlySet<string>? DependencyFilter = null);

/// <summary>The Update column's state for a mod, in the order the
/// WinForms front end tested them.</summary>
public enum ModUpdateState
{
    /// <summary>A built-in mod, updated with the manager.</summary>
    BuiltIn,
    /// <summary>No VostokMods source: can't be checked until linked.</summary>
    Unlinked,
    /// <summary>Linked, but the last update check didn't cover it.</summary>
    Unknown,
    UpToDate,
    /// <summary>mod.txt and VostokMods write the version in formats that
    /// can't be ordered ("0.0.420" vs "0.4.20_R"): most likely the same
    /// release written two ways.</summary>
    Ambiguous,
    /// <summary>The local version is newer than the listed one (a dev
    /// build, a manual bump, or the API cache lagging).</summary>
    NewerLocal,
    Outdated,
}

/// <summary>A mod's update state, the latest version VostokMods lists
/// (when known) and the explanation the front end shows on hover.</summary>
public sealed record ModUpdateInfo(ModUpdateState State, string Latest, string Tooltip);

/// <summary>One row of the mods list: a mod, or the header of a named
/// pack (Entry is null; PackName / PackCount / PackCollapsed describe
/// it). Mods inside an expanded pack follow their header with InPack set.</summary>
public sealed record ModRow
{
    public ModEntry? Entry { get; init; }
    public bool IsPackHeader => Entry == null;

    // ── pack header ──
    public string PackName { get; init; } = "";
    public int PackCount { get; init; }
    public bool PackCollapsed { get; init; }

    // ── mod ──
    /// <summary>The display name, else the file name.</summary>
    public string DisplayName { get; init; } = "";
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";
    public int Priority { get; init; }
    public bool Enabled { get; init; }
    /// <summary>1-based load position among the enabled rows shown, or
    /// null for a disabled mod.</summary>
    public int? Position { get; init; }
    public bool Locked { get; init; }
    public bool BuiltIn { get; init; }
    public bool Testing { get; init; }
    public bool InstalledThisSession { get; init; }
    public bool InPack { get; init; }
    /// <summary>The user's personal note, or "" when there is none.</summary>
    public string Note { get; init; } = "";
    public ModUpdateInfo? Update { get; init; }
    public bool InConflict { get; init; }
}

/// <summary>The mods list: the rows to show (pack headers interleaved)
/// and the mods in display order without headers, the order a drag
/// reorder works in (a collapsed pack's mods are in it too).</summary>
public sealed record ModListView(IReadOnlyList<ModRow> Rows, IReadOnlyList<ModEntry> Displayed);

/// <summary>The mods status line's numbers.</summary>
public sealed record ModCounts(int Total, int Enabled)
{
    public int Disabled => Total - Enabled;
    public string Summary => $"Mods: {Total} found  ({Enabled} enabled, {Disabled} disabled)";
}

/// <summary>One mod whose priority a reorder changes.</summary>
public sealed record PriorityChange(ModEntry Entry, int OldPriority, int NewPriority);

/// <summary>Per-file progress of an install batch.</summary>
public sealed record InstallProgress(int Done, int Total, string FileName);

/// <summary>What an install batch did. `InstalledModIds` are the mod ids
/// that landed, for the missing-dependency follow-up.</summary>
public sealed record InstallResult(
    IReadOnlyList<string> Installed,
    IReadOnlyList<(string Path, string Reason)> Skipped,
    IReadOnlyList<string> InstalledModIds)
{
    public static InstallResult Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<(string, string)>(), Array.Empty<string>());
}

/// <summary>What a file drop did: the .vmz files were installed
/// (`Install`, null when there were none); the modpack .zip files and
/// mod-list .json files are left for the front end to hand to the
/// modpack / mod-list import, one session per file.</summary>
public sealed record DropResult(
    InstallResult? Install,
    IReadOnlyList<string> ModpackFiles,
    IReadOnlyList<string> ModListFiles);

/// <summary>The callbacks the embedded VostokMods browser needs: install
/// a downloaded file (remembering its source) and whether a source is
/// already installed or in the Library.</summary>
public sealed record ModBrowserHooks(
    Func<string, ModSource, Task> InstallDownloaded,
    Func<ModSource, bool> OwnsSource);
