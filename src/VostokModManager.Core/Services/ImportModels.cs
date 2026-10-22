// Plain data for importing mod lists and modpacks into a profile: where
// the import goes, the per-row plan the user ticks, and the outcome.
// Replaces Ui/ImportTargetDialog, Ui/ImportModListDialog's plan grid and
// Ui/ModpackBrowserDialog's list.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Services;

/// <summary>Which profile an import lands in.</summary>
public enum ImportTarget
{
    ActiveProfile,
    /// <summary>A new profile (named after the pack), which becomes active.</summary>
    NewProfile,
    /// <summary>Another saved profile, which becomes active.</summary>
    ExistingProfile,
}

/// <summary>What the target question offers: the active profile, a new
/// one under `SuggestedNewName` (unique among the saved ones), or one
/// of `Others`.</summary>
public sealed record ImportTargetOptions(
    string PackName, ModProfile Active, IReadOnlyList<ModProfile> Others, string SuggestedNewName);

/// <summary>The user's answer to the target question.</summary>
public sealed record ImportTargetChoice(ImportTarget Target, string NewProfileName = "", ModProfile? ExistingProfile = null);

/// <summary>What importing one list entry would do:
///   AlreadyInProfile  the profile lists it; nothing changes
///   LivePresent       live on disk but not in the profile; profile entry only
///   FromLibrary       a downloaded copy is in the Library; copied live
///   Download          fetched from VostokMods
///   Unresolvable      no id match and no source; skipped</summary>
public enum ImportRowKind { AlreadyInProfile, LivePresent, FromLibrary, Download, Unresolvable }

/// <summary>One entry of an import plan. `Apply` is the user's tick,
/// read only for checkable rows.</summary>
public sealed class ImportPlanRow
{
    internal ImportPlanRow(
        ImportRowKind kind, ImportEntry entry, List<string> parentChain, ModEntry? live,
        string libraryPath, string icon, string status, bool optIn)
    {
        Kind = kind;
        Entry = entry;
        ParentChain = parentChain;
        Live = live;
        LibraryPath = libraryPath;
        StatusIcon = icon;
        StatusText = status;
        Apply = IsCheckable && optIn;
    }

    public ImportRowKind Kind { get; }
    public ImportEntry Entry { get; }
    /// <summary>Empty for a top-level entry; else the mods whose
    /// dependency list brought it in, outermost first.</summary>
    public IReadOnlyList<string> ParentChain { get; }
    public ModEntry? Live { get; }
    public string LibraryPath { get; }
    public string StatusIcon { get; }
    public string StatusText { get; }
    public bool Apply { get; set; }

    public bool IsCheckable => Kind is ImportRowKind.LivePresent or ImportRowKind.FromLibrary or ImportRowKind.Download;
    public bool WillAct => IsCheckable && Apply;
    /// <summary>Optional entries start unticked; a list without the flag
    /// counts as required.</summary>
    public bool IsOptional => Entry.IsOptional == true;

    public string Label => string.IsNullOrEmpty(Entry.DisplayName) ? Entry.ModId : Entry.DisplayName;

    /// <summary>"" for top-level, "↳ dep of X → Y" for a dependency
    /// (the middle of a long chain elided).</summary>
    public string Via
    {
        get
        {
            if (ParentChain.Count == 0) return "";
            IEnumerable<string> parts = ParentChain;
            if (ParentChain.Count > 3) parts = new[] { ParentChain[0], "…", ParentChain[^1] };
            return "↳ dep of " + string.Join(" → ", parts);
        }
    }
}

/// <summary>An import plan for the active profile. Built by
/// ManagerSession.BuildImportPlan; run by ExecuteImportAsync.</summary>
public sealed class ImportPlan
{
    internal ImportPlan(ModListImport import, ModProfile profile, string? companionDir, List<ImportPlanRow> rows)
    {
        Import = import;
        Profile = profile;
        CompanionDir = companionDir;
        Rows = rows;
    }

    public ModListImport Import { get; }
    public ModProfile Profile { get; }
    public string? CompanionDir { get; }
    public IReadOnlyList<ImportPlanRow> Rows { get; }

    /// <summary>"Import 'pack' → 'profile'".</summary>
    public string Title => !string.IsNullOrEmpty(Import.Name)
        ? $"Import '{Import.Name}'  →  '{Profile.Name}'"
        : $"Import mod list → '{Profile.Name}'";

    public bool AnyLineage => Rows.Any(r => r.ParentChain.Count > 0);

    public string Summary
    {
        get
        {
            int Count(ImportRowKind k) => Rows.Count(r => r.Kind == k);
            var parts = new List<string>();
            void Add(int n, string text) { if (n > 0) parts.Add($"{n} {text}"); }
            Add(Count(ImportRowKind.AlreadyInProfile), "already in profile");
            Add(Count(ImportRowKind.LivePresent),      "live → add to profile");
            Add(Count(ImportRowKind.FromLibrary),      "from library");
            Add(Count(ImportRowKind.Download),         "to download");
            Add(Count(ImportRowKind.Unresolvable),     "unresolvable");
            var topLevel = Rows.Count(r => r.ParentChain.Count == 0);
            var transit = Rows.Count - topLevel;
            var required = Rows.Count(r => !r.IsOptional);
            var optional = Rows.Count - required;
            var head = string.IsNullOrEmpty(Import.Description) ? "" : $"  \"{Import.Description}\"  ";
            var lineage = transit > 0 ? $"  ({topLevel} top-level, {transit} via deps)" : "";
            var split = optional > 0
                ? $"  · {required} required + {optional} optional (optionals unticked by default)"
                : "";
            return $"{head}{string.Join(", ", parts)} — {Rows.Count} entries total{lineage}{split}.";
        }
    }

    /// <summary>Ticks every checkable row.</summary>
    public void SelectAll() => SetAll(includeOptional: true, ticked: true);

    /// <summary>Unticks every checkable row.</summary>
    public void SelectNone() => SetAll(includeOptional: true, ticked: false);

    /// <summary>Ticks the required rows and unticks the optional ones.</summary>
    public void SelectRequiredOnly() => SetAll(includeOptional: false, ticked: true);

    private void SetAll(bool includeOptional, bool ticked)
    {
        foreach (var row in Rows)
        {
            if (!row.IsCheckable) continue;
            row.Apply = !includeOptional && row.IsOptional ? false : ticked;
        }
    }
}

/// <summary>Outcome of ExecuteImportAsync. `InstalledModIds` are the
/// manifest ids added to or refreshed in the profile; the caller scans
/// their declared dependencies. `Applied` is true when the profile
/// changed and the run wasn't cancelled.</summary>
public sealed record ImportResult(
    bool Applied, bool Cancelled, IReadOnlyList<string> InstalledModIds, IReadOnlyList<string> Log);

/// <summary>The modpack list from VostokMods. `Error` is set (and
/// `Packs` empty) when it couldn't be read; `Header` is the line the
/// old browser showed above the list.</summary>
public sealed record ModpackListing(IReadOnlyList<VmModpackSummary> Packs, string Header, string? Error);
