// Plain data the Updates / links / dependencies / conflicts / drift /
// self-update area hands to the front end. Each record replaces what a
// WinForms dialog or status label used to compute for itself, so a new
// UI only has to render it.

using VostokModManager.Domain;

namespace VostokModManager.Services;

// ── downloads ─────────────────────────────────────────────────────────

/// <summary>How far the download of the mod at `Path` has got. `Total`
/// is 0 while the size is unknown (the UI shows a sliding band then).</summary>
public sealed record DownloadProgress(string Path, long Done, long Total)
{
    /// <summary>0–100, or -1 while the total is unknown.</summary>
    public int Percent => Total > 0 ? (int)Math.Clamp(Done * 100.0 / Total, 0, 100) : -1;

    /// <summary>What the mod's Update cell shows while downloading.</summary>
    public string CellText => Total > 0 ? $"{Percent}%" : "…";

    /// <summary>The status-line text for a download of `label`.</summary>
    public string StatusText(string label)
        => Total > 0
            ? $"Downloading {label} … {Percent}%  ({ManagerSession.FormatBytes(Done)} / {ManagerSession.FormatBytes(Total)})"
            : Done > 0
                ? $"Downloading {label} … {ManagerSession.FormatBytes(Done)}"
                : $"Downloading {label} …";
}

// ── update state of one mod ───────────────────────────────────────────

public enum UpdateStatus
{
    /// <summary>Built into the manager; updated with it.</summary>
    BuiltIn,
    /// <summary>No VostokMods source; clicking links it.</summary>
    Unlinked,
    /// <summary>Linked, but the last check didn't cover it.</summary>
    Unknown,
    Current,
    /// <summary>The two version strings can't be ordered (likely the
    /// same release written two ways).</summary>
    Mismatch,
    /// <summary>The local file is newer than the listing.</summary>
    Ahead,
    Outdated,
}

/// <summary>The Update cell of one mod: glyph, state and hover text.</summary>
public sealed record UpdateCellState(UpdateStatus Status, string Glyph, string Tooltip, string Latest);

/// <summary>Counts behind the Updates status line.</summary>
public sealed record UpdateSummary(int Outdated, int Current, int Mismatch, int Unknown, int Unlinked);

/// <summary>The outcome of updating (or reverting) one mod.</summary>
public sealed record ModUpdateResult(
    string ModId, string Label, bool Ok, string OldVersion, string NewVersion, string Message);

// ── backups (Revert to previous version) ──────────────────────────────

/// <summary>One on-disk snapshot of a mod, with the labels the revert
/// list shows.</summary>
public sealed record ModBackupView(string Path, string ModId, string Version, DateTime CreatedAt, long SizeBytes)
{
    public string VersionLabel => string.IsNullOrEmpty(Version) ? "?" : $"v{Version}";

    public string SizeLabel
        => SizeBytes < 1024 ? $"{SizeBytes} B"
            : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:F1} KB"
            : $"{SizeBytes / 1024.0 / 1024.0:F1} MB";

    public string WhenLabel
    {
        get
        {
            if (CreatedAt == DateTime.MinValue) return "unknown";
            var ago = DateTime.Now - CreatedAt;
            string rel;
            if (ago.TotalMinutes < 1) rel = "just now";
            else if (ago.TotalMinutes < 60) rel = $"{(int)ago.TotalMinutes} min ago";
            else if (ago.TotalHours < 24) rel = $"{(int)ago.TotalHours} h ago";
            else if (ago.TotalDays < 30) rel = $"{(int)ago.TotalDays} d ago";
            else rel = CreatedAt.ToString("yyyy-MM-dd");
            return $"{CreatedAt:yyyy-MM-dd HH:mm}  ·  {rel}";
        }
    }

    internal static ModBackupView From(ModBackup.BackupEntry b)
        => new(b.Path, b.ModId, b.Version, b.CreatedAt, b.SizeBytes);
}

// ── dependencies ──────────────────────────────────────────────────────

public enum DependencyState { Enabled, Disabled, NotInstalled }

/// <summary>One declared dependency of a mod and its live state (the
/// Dependencies dialog's rows).</summary>
public sealed record DependencyView(string ModId, bool Required, DependencyState State)
{
    public string Kind => Required ? "required" : "optional";

    public string Glyph => State switch
    {
        DependencyState.Enabled => "✓",
        DependencyState.Disabled => "⚠",
        _ => "✗",
    };

    public string StatusText => State switch
    {
        DependencyState.Enabled => "Installed and enabled",
        DependencyState.Disabled => "Installed but currently disabled",
        _ => Required
            ? "Not installed — required, install or disable the dependent"
            : "Not installed (optional)",
    };
}

/// <summary>A mod's dependencies: the header line and the rows.</summary>
public sealed record DependencyReport(string ModLabel, string Header, IReadOnlyList<DependencyView> Rows);

/// <summary>One row of the required-dependency picker: a mod id that
/// can be ticked, and whether it is ticked now.</summary>
public sealed record DependencyChoice(string ModId, bool Checked, bool Installed);

// ── conflicts ─────────────────────────────────────────────────────────

/// <summary>Three tiers used to group conflicts. Blocking = the game
/// refuses to load (class_name) or a mod is inert until the user acts
/// (missing_dependency); Behavior = a winner silently overrides others;
/// Info = advisory load-order constraints.</summary>
public enum ConflictSeverity { Blocking, Behavior, Info }

/// <summary>How the "wins" text should be coloured.</summary>
public enum WinnerTone { Default, Muted, HardFail, Winner }

/// <summary>Which mod currently wins a conflict and why.</summary>
public sealed record ConflictWinner(string Text, WinnerTone Tone, string Why);

/// <summary>One conflict as the Conflicts list shows it. RawTooltip is
/// "[type]  key: …" for power users; ModNames are display names,
/// earliest-loading first, with unresolvable ids (missing mods, file
/// names) last; HasResolveButton is true where a Resolve button belongs
/// (AI edition, file_overlap only).</summary>
public sealed record ConflictView(
    ConflictDetector.Conflict Conflict,
    ConflictSeverity Severity,
    string Title,
    string Subtitle,
    string RawTooltip,
    IReadOnlyList<string> ModNames,
    ConflictWinner Winner,
    bool HasResolveButton,
    string ResolveHint)
{
    public string Type => Conflict.Type;
    public string Key => Conflict.Key;
    public IReadOnlyList<string> ModIds => Conflict.ModIds;
}

/// <summary>A method hooked on the conflict's script, with the mods
/// hooking it. Two or more mods on one method is a real collision.</summary>
public sealed record HookMethodRow(string Method, IReadOnlyList<string> ModNames)
{
    public bool IsCollision => ModNames.Count >= 2;
    public string StatusText => IsCollision ? $"⚠ COLLISION ({ModNames.Count} mods)" : "ok — coexists";
}

/// <summary>A method the last play session's log shows several mods
/// hooking, in load order (last wins).</summary>
public sealed record RuntimeHookRow(string Method, string ModsText);

/// <summary>A mod and its conflicting value (file path, autoload name, …).</summary>
public sealed record ConflictValueRow(string ModName, string Value);

/// <summary>Everything the conflict detail view shows. Hook collisions
/// fill Methods + the runtime section; every other type fills Values.</summary>
public sealed record ConflictDetail(
    string Title,
    string Subtitle,
    IReadOnlyList<string> ModNames,
    bool IsHook,
    string Legend,
    IReadOnlyList<HookMethodRow> Methods,
    string RuntimeNote,
    IReadOnlyList<RuntimeHookRow> RuntimeRows,
    IReadOnlyList<ConflictValueRow> Values);

// ── drift ─────────────────────────────────────────────────────────────

/// <summary>One difference between the active profile and what is live.</summary>
public sealed record DriftItem(string ModId, string DisplayName, string Description);

/// <summary>The drift status line: shown only when there is drift.</summary>
public sealed record DriftStatus(string ProfileName, IReadOnlyList<DriftItem> Items, string Text)
{
    public int Count => Items.Count;
}

// ── self-update (mod loader + manager) ────────────────────────────────

public enum VersionState { Unknown, UpToDate, Ahead, Outdated }

/// <summary>The mod loader's status line: installed vs latest release.</summary>
public sealed record MmlStatus(string Installed, string Latest, bool FromCache, VersionState State, string Text)
{
    public bool IsOutdated => State == VersionState.Outdated;
}

/// <summary>The manager's status line: running vs latest release.</summary>
public sealed record ManagerStatus(
    string Installed, string Latest, bool FromCache, bool NoRelease, VersionState State, string Text)
{
    public bool IsOutdated => State == VersionState.Outdated;
}

/// <summary>What the front end should do after a click on a
/// self-update status line: open a page, exit so the swap script can
/// replace the .exe, or nothing.</summary>
public sealed record SelfUpdateAction(string? OpenUrl, bool MustExit)
{
    public static readonly SelfUpdateAction None = new(null, false);
    public static SelfUpdateAction Open(string url) => new(url, false);
}

/// <summary>The outcome of the manager's auto-update. When `MustExit`
/// is true the swap script is running and waits for this process to
/// exit; the front end must shut down now (Application.Exit in the
/// WinForms build) so the script can copy the new .exe in place.</summary>
public sealed record ManagerUpdateResult(bool MustExit, string ScriptPath, string Message)
{
    public static ManagerUpdateResult Aborted(string message) => new(false, "", message);
}

// ── description + changelog ───────────────────────────────────────────

/// <summary>A fetched VostokMods description. `Text` is markdown (or a
/// placeholder in parentheses when there is none); `Status` is the line
/// under it.</summary>
public sealed record DescriptionResult(bool Ok, string Text, string Status);

/// <summary>A changelog read from inside a mod's archive.</summary>
public sealed record ModChangelog(string ModLabel, string SourceFile, string Text);
