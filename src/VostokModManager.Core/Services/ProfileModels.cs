// Plain data the profile area hands to a front end: profile cards, the
// version-bump candidates offered on a switch, the N-profile comparison
// matrix, the dependency rollup and the Apply plan. The WinForms dialogs
// these replace rendered the same data straight into their grids.

using VostokModManager.Domain;

namespace VostokModManager.Services;

// ── profile cards ─────────────────────────────────────────────────────

/// <summary>One saved profile as a card shows it. `SaveSlots` are the
/// Save Slots slots tied to this profile (a slot's Profile field names
/// it); occupied or not, the card decides what to show.</summary>
public sealed record ProfileSummary(
    ModProfile Profile,
    string Name,
    string Description,
    int ModCount,
    int EnabledCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    bool IsActive,
    int BundledArchives,
    long BundledBytes,
    IReadOnlyList<SaveSlots.Slot> SaveSlots);

/// <summary>One mod of a profile as the profile detail list shows it.
/// `Note` is the line the old detail grid put in its tooltip (bundled
/// archive, or where Apply would fetch it from).</summary>
public sealed record ProfileModView(ProfileMod Mod, bool HasBundle, string Note);

/// <summary>Outcome of copying mods into another profile (drag-drop).</summary>
public sealed record ProfileCopyResult(int Added, int Skipped);

/// <summary>Outcome of importing a profile file. `ApplyRequested` is
/// true when the user said yes to "Apply it now?" (a .json spec names
/// mods but brings no archives); the front end then opens the Apply
/// plan for `Profile`.</summary>
public sealed record ProfileImportResult(
    ModProfile Profile,
    bool IsBundle,
    int LibraryAdded,
    int LibrarySkipped,
    int MissingFromLibrary,
    bool ApplyRequested);

/// <summary>What a profile export writes.</summary>
public enum ProfileExportFormat
{
    /// <summary>.vmprofile: profile.json plus the mod archives.</summary>
    Bundle,
    /// <summary>.zip for the mod loader's Modpacks tab: the mod list
    /// only; the loader downloads each mod from its source.</summary>
    LoaderModpack,
    /// <summary>.json: profile.json only, no archive references.</summary>
    Json,
}

/// <summary>A profile mod pinned to a version older than the newest
/// one of its lineage in the Library, offered on a switch.</summary>
public sealed record NewerVersionCandidate(
    ProfileMod Mod, string NewestVersion, string NewestDisplayName, bool IsDevBuild);

// ── dependency rollup ─────────────────────────────────────────────────

public enum DependencyStatus { Enabled, Disabled, Missing }

/// <summary>One declared dependency and its live state.</summary>
public sealed record DependencyState(string DepId, bool Required, DependencyStatus Status, string StatusText)
{
    public string Icon => Status switch
    {
        DependencyStatus.Enabled  => "✓",
        DependencyStatus.Disabled => "⚠",
        _                         => "✗",
    };
}

/// <summary>A mod of the active profile that declares dependencies.</summary>
public sealed record ProfileDependent(ModEntry Mod, string Label, IReadOnlyList<DependencyState> Dependencies);

/// <summary>Every mod in the active profile (plus locked ones) that
/// declares dependencies, with each dependency's state.
/// `ProfileName` is null when there is no active profile.</summary>
public sealed record ProfileDependencyReport(
    string? ProfileName,
    IReadOnlyList<ProfileDependent> Dependents,
    int MissingRequired,
    int DisabledRequired,
    string Summary);

// ── comparison matrix ─────────────────────────────────────────────────

/// <summary>One profile's entry for one mod; `Mod` is null when the
/// profile doesn't list it.</summary>
public sealed record ProfileMatrixCell(ProfileMod? Mod)
{
    public bool Present => Mod != null;
    public string Version => Mod?.Version ?? "";
    public bool Enabled => Mod?.IsEnabled ?? false;
    public int Priority => Mod?.Priority ?? 0;

    /// <summary>"v1.2  prio 3  on", or "(absent)".</summary>
    public string Text
    {
        get
        {
            if (Mod == null) return "(absent)";
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(Mod.Version)) bits.Add($"v{Mod.Version}");
            bits.Add($"prio {Mod.Priority}");
            bits.Add(Mod.IsEnabled ? "on" : "off");
            return string.Join("  ", bits);
        }
    }
}

/// <summary>One mod across the compared profiles; `Cells` is in the
/// order of ProfileComparison.Profiles. `What` describes the
/// difference ("" when every profile agrees).</summary>
public sealed record ProfileMatrixRow(
    string ModId,
    string DisplayName,
    IReadOnlyList<ProfileMatrixCell> Cells,
    bool HasDifferences,
    string What);

/// <summary>Every mod present in any of the compared profiles.</summary>
public sealed record ProfileComparison(IReadOnlyList<ModProfile> Profiles, IReadOnlyList<ProfileMatrixRow> Rows)
{
    public int DifferenceCount => Rows.Count(r => r.HasDifferences);

    /// <summary>The rows a grid shows: with `onlyDifferences`, the ones
    /// that differ; with `text`, the ones whose name, id, difference or
    /// any cell contains it (case-insensitive).</summary>
    public IEnumerable<ProfileMatrixRow> Filter(bool onlyDifferences, string? text = null)
    {
        var f = (text ?? "").Trim();
        foreach (var r in Rows)
        {
            if (onlyDifferences && !r.HasDifferences) continue;
            if (f.Length > 0
                && !r.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
                && !r.ModId.Contains(f, StringComparison.OrdinalIgnoreCase)
                && !r.What.Contains(f, StringComparison.OrdinalIgnoreCase)
                && !r.Cells.Any(c => c.Text.Contains(f, StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return r;
        }
    }

    /// <summary>"Profiles are identical …" or the counts line.</summary>
    public string Summary
    {
        get
        {
            var diff = DifferenceCount;
            if (diff == 0)
                return "Profiles are identical — same mods, same versions, same priorities, same enabled state.";
            if (Profiles.Count == 2)
            {
                var onlyA = Rows.Count(r => r.Cells[0].Present && !r.Cells[1].Present);
                var onlyB = Rows.Count(r => !r.Cells[0].Present && r.Cells[1].Present);
                var both = diff - onlyA - onlyB;
                return $"{onlyA} only in '{Profiles[0].Name}'  ·  {onlyB} only in '{Profiles[1].Name}'  ·  "
                    + $"{both} different — {diff} differences total.";
            }
            var partial = Rows.Count(r => r.HasDifferences && r.Cells.Any(c => !c.Present));
            return $"{partial} not in every profile  ·  {diff - partial} different — "
                + $"{diff} differences total across {Profiles.Count} profiles.";
        }
    }
}

// ── apply plan ────────────────────────────────────────────────────────

/// <summary>What Apply does with one profile mod. Row states:
///   Match       installed, version OK (or no way to switch); applies enabled/priority
///   Replace*    installed at the wrong version; overwrite from bundle / library
///   Install*    not installed; copy in from bundle / library
///   UpdateDownload / MissingDownload  fetch from VostokMods
///   NoSource    nothing to install from; skipped
///   LockedSkip  locked mod; file and cfg state untouched</summary>
public enum ApplyRowKind
{
    Match,
    ReplaceFromBundle,
    InstallFromBundle,
    ReplaceFromLibrary,
    InstallFromLibrary,
    UpdateDownload,
    MissingDownload,
    NoSource,
    LockedSkip,
}

/// <summary>The per-row Source choice the Apply plan offers.</summary>
public enum ApplySource { KeepCurrent, UseBundle, UseLibrary, Download, Skip, Locked }

/// <summary>One profile mod in an Apply plan. `Source` and `Apply` are
/// the user's choices; changing `Source` re-derives `Kind` and ticks
/// `Apply` when the new kind does something, as the dialog's dropdown did.</summary>
public sealed class ApplyPlanRow
{
    private ApplySource _source;
    private bool _overridden;

    internal ApplyPlanRow(
        ApplyRowKind kind, ProfileMod profileMod, ModEntry? installed,
        string bundlePath, string libraryPath, string icon, string status, bool optIn)
    {
        DefaultKind = kind;
        ProfileMod = profileMod;
        Installed = installed;
        BundlePath = bundlePath;
        LibraryPath = libraryPath;
        DefaultIcon = icon;
        DefaultStatus = status;
        SourceChoices = SourceChoicesFor(this);
        _source = DefaultSourceFor(kind);
        Apply = IsCheckableKind(kind) && optIn;
    }

    public ProfileMod ProfileMod { get; }
    /// <summary>The live mod with this id, or null when not installed.</summary>
    public ModEntry? Installed { get; }
    /// <summary>The profile's bundled archive, or "".</summary>
    public string BundlePath { get; }
    /// <summary>A Library copy (exact version, else the newest), or "".</summary>
    public string LibraryPath { get; }
    public ApplyRowKind DefaultKind { get; }
    public string DefaultIcon { get; }
    public string DefaultStatus { get; }
    public IReadOnlyList<ApplySource> SourceChoices { get; }

    /// <summary>True when the Source can't be changed (one choice, or locked).</summary>
    public bool SourceIsFixed => SourceChoices.Count <= 1 || DefaultKind == ApplyRowKind.LockedSkip;

    public ApplySource Source
    {
        get => _source;
        set
        {
            if (!SourceChoices.Contains(value))
                throw new ArgumentException($"'{value}' isn't offered for {ProfileMod.DisplayName}.", nameof(value));
            if (value == _source) return;
            _source = value;
            _overridden = true;
            // Checkable kinds auto-tick (the user just picked an active
            // source); the others can't be ticked.
            Apply = IsCheckableKind(Kind);
        }
    }

    /// <summary>The opt-in tick. Only read for kinds that do something
    /// to a file (IsCheckable).</summary>
    public bool Apply { get; set; }

    public ApplyRowKind Kind => _overridden ? ResolveKindFromSource(_source) : DefaultKind;
    public bool IsCheckable => IsCheckableKind(Kind);
    public bool WillAct => IsCheckable && Apply;

    public string StatusIcon => _overridden ? LabelsForKind(Kind).Icon : DefaultIcon;
    public string StatusText => _overridden ? LabelsForKind(Kind).Status : DefaultStatus;

    internal static bool IsCheckableKind(ApplyRowKind k) =>
        k is ApplyRowKind.ReplaceFromBundle
            or ApplyRowKind.InstallFromBundle
            or ApplyRowKind.ReplaceFromLibrary
            or ApplyRowKind.InstallFromLibrary
            or ApplyRowKind.UpdateDownload
            or ApplyRowKind.MissingDownload;

    /// <summary>The choices a row offers, gated by what's possible.
    /// Locked rows only "Locked"; rows with nothing to install from
    /// only "Skip".</summary>
    private static List<ApplySource> SourceChoicesFor(ApplyPlanRow row)
    {
        if (row.DefaultKind == ApplyRowKind.LockedSkip)
            return new List<ApplySource> { ApplySource.Locked };
        var choices = new List<ApplySource>();
        if (row.Installed != null)                     choices.Add(ApplySource.KeepCurrent);
        if (!string.IsNullOrEmpty(row.BundlePath))     choices.Add(ApplySource.UseBundle);
        if (!string.IsNullOrEmpty(row.LibraryPath))    choices.Add(ApplySource.UseLibrary);
        if (row.ProfileMod.SourceRef.IsValid)          choices.Add(ApplySource.Download);
        if (choices.Count == 0)                        choices.Add(ApplySource.Skip);
        return choices;
    }

    private static ApplySource DefaultSourceFor(ApplyRowKind kind) => kind switch
    {
        ApplyRowKind.LockedSkip         => ApplySource.Locked,
        ApplyRowKind.Match              => ApplySource.KeepCurrent,
        ApplyRowKind.ReplaceFromBundle  => ApplySource.UseBundle,
        ApplyRowKind.InstallFromBundle  => ApplySource.UseBundle,
        ApplyRowKind.ReplaceFromLibrary => ApplySource.UseLibrary,
        ApplyRowKind.InstallFromLibrary => ApplySource.UseLibrary,
        ApplyRowKind.UpdateDownload     => ApplySource.Download,
        ApplyRowKind.MissingDownload    => ApplySource.Download,
        _                               => ApplySource.Skip,
    };

    /// <summary>A Source choice → the kind the executor acts on;
    /// context-aware (installed → Replace / Update, else Install / Missing).</summary>
    private ApplyRowKind ResolveKindFromSource(ApplySource source)
    {
        var isInstalled = Installed != null;
        return source switch
        {
            ApplySource.KeepCurrent => ApplyRowKind.Match,
            ApplySource.UseBundle   => isInstalled ? ApplyRowKind.ReplaceFromBundle : ApplyRowKind.InstallFromBundle,
            ApplySource.UseLibrary  => isInstalled ? ApplyRowKind.ReplaceFromLibrary : ApplyRowKind.InstallFromLibrary,
            ApplySource.Download    => isInstalled ? ApplyRowKind.UpdateDownload : ApplyRowKind.MissingDownload,
            ApplySource.Locked      => ApplyRowKind.LockedSkip,
            ApplySource.Skip        => ApplyRowKind.NoSource,
            _                       => DefaultKind,
        };
    }

    private (string Icon, string Status) LabelsForKind(ApplyRowKind kind) => kind switch
    {
        ApplyRowKind.Match              => ("✓", "Keep installed version"),
        ApplyRowKind.ReplaceFromBundle  => ("↔", $"Replace from bundle (installed v{Installed?.Version} → v{ProfileMod.Version})"),
        ApplyRowKind.InstallFromBundle  => ("⬇", "Install from bundle"),
        ApplyRowKind.ReplaceFromLibrary => ("↔", $"Replace from library (installed v{Installed?.Version} → v{ProfileMod.Version})"),
        ApplyRowKind.InstallFromLibrary => ("⬇", "Install from library (already downloaded)"),
        ApplyRowKind.UpdateDownload     => ("⬆", "Download latest from VostokMods"),
        ApplyRowKind.MissingDownload    => ("⬇", "Download from VostokMods"),
        ApplyRowKind.NoSource           => ("✗", "Skipped"),
        ApplyRowKind.LockedSkip         => ("🔒", "Locked — file + state untouched"),
        _                               => (DefaultIcon, DefaultStatus),
    };
}

/// <summary>What applying a profile would do, row by row. Built by
/// ManagerSession.BuildApplyPlan; the user edits the rows' Source and
/// Apply, then ExecuteApplyAsync runs it.</summary>
public sealed class ApplyPlan
{
    internal ApplyPlan(ModProfile profile, List<ApplyPlanRow> rows, IReadOnlyCollection<string> lockedModIds)
    {
        Profile = profile;
        Rows = rows;
        LockedModIds = lockedModIds;
    }

    public ModProfile Profile { get; }
    public IReadOnlyList<ApplyPlanRow> Rows { get; }
    /// <summary>Mod ids neither wiped nor touched (locked + built-in).</summary>
    public IReadOnlyCollection<string> LockedModIds { get; }

    public string Summary
    {
        get
        {
            int Count(ApplyRowKind k) => Rows.Count(r => r.DefaultKind == k);
            var parts = new List<string>();
            void Add(int n, string text) { if (n > 0) parts.Add($"{n} {text}"); }
            Add(Count(ApplyRowKind.Match),              "matching");
            Add(Count(ApplyRowKind.ReplaceFromBundle),  "from bundle (replace)");
            Add(Count(ApplyRowKind.InstallFromBundle),  "from bundle (install)");
            Add(Count(ApplyRowKind.ReplaceFromLibrary), "from library (replace)");
            Add(Count(ApplyRowKind.InstallFromLibrary), "from library (install)");
            Add(Count(ApplyRowKind.UpdateDownload),     "update via VostokMods");
            Add(Count(ApplyRowKind.MissingDownload),    "download via VostokMods");
            Add(Count(ApplyRowKind.NoSource),           "unavailable");
            Add(Count(ApplyRowKind.LockedSkip),         "🔒 locked (skipped)");
            var desc = string.IsNullOrEmpty(Profile.Description) ? "" : $"  \"{Profile.Description}\"  ";
            return $"{desc}{string.Join(", ", parts)} — {Profile.Mods.Count} mods total.";
        }
    }
}

/// <summary>Outcome of ExecuteApplyAsync. `Applied` is false when it
/// was cancelled (partial changes may have been made).</summary>
public sealed record ApplyResult(bool Applied, int Wiped, IReadOnlyList<string> Log);
