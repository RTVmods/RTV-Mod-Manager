using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A pack mod the active profile doesn't have yet.</summary>
public sealed record ImportMissingRow(string Name, string Version, string How, string Via, bool CantInstall);

/// <summary>The first step of a mod list or modpack import: what the active
/// profile is missing from it, and where it should go (the active profile,
/// a new profile of its own, or another saved profile).</summary>
public sealed class ImportTargetViewModel
{
    public ImportTargetViewModel(ImportTargetOptions options, ImportPlan? preview, bool isModpack)
    {
        Options = options;
        Kind = isModpack ? "INSTALL MODPACK" : "IMPORT MOD LIST";
        PackName = string.IsNullOrEmpty(options.PackName) ? "This mod list" : options.PackName;
        var active = options.Active.Name;

        var rows = preview?.Rows ?? (IReadOnlyList<ImportPlanRow>)Array.Empty<ImportPlanRow>();
        Missing = rows.Where(r => r.Kind != ImportRowKind.AlreadyInProfile)
            .Select(r => new ImportMissingRow(r.Label, r.Entry.Version, How(r), r.Via, r.Kind == ImportRowKind.Unresolvable))
            .ToList();
        var have = rows.Count - Missing.Count;
        Summary = rows.Count == 0 ? ""
            : $"{rows.Count} mod{(rows.Count == 1 ? "" : "s")}  ·  {have} already in '{active}'  ·  {Missing.Count} missing";
        MissingHeader = $"MISSING FROM '{active.ToUpperInvariant()}'";
        NothingMissing = $"Everything in it is already in '{active}'.";

        AddTitle = $"Add to '{active}'";
        AddDetail = Missing.Count == 0
            ? "Your loadout already has every mod in it; nothing to install."
            : $"Install the {Missing.Count} missing mod{(Missing.Count == 1 ? "" : "s")} into your loadout, the active profile. Nothing already there changes.";
        NewTitle = $"New profile '{options.SuggestedNewName}'";
        NewDetail = $"A profile of its own with every mod in {(isModpack ? "the pack" : "the list")}, which becomes the active one. Mods you already have are reused, not downloaded again.";
        OtherDetail = "Add it to one of your other saved profiles, which then becomes the active one.";
        HasOthers = options.Others.Count > 0;
    }

    public ImportTargetOptions Options { get; }
    public string Kind { get; }
    public string PackName { get; }
    public string Summary { get; }
    public IReadOnlyList<ImportMissingRow> Missing { get; }
    public bool HasMissing => Missing.Count > 0;
    public string MissingHeader { get; }
    public string NothingMissing { get; }
    public string AddTitle { get; }
    public string AddDetail { get; }
    public string NewTitle { get; }
    public string NewDetail { get; }
    public string OtherDetail { get; }
    public bool HasOthers { get; }

    private static string How(ImportPlanRow r) => r.Kind switch
    {
        ImportRowKind.LivePresent => "On disk already; added to the profile",
        ImportRowKind.FromLibrary => "From My Mods (already downloaded)",
        ImportRowKind.Download => "Download from VostokMods",
        _ => "Not on VostokMods; can't be installed",
    } + (r.IsOptional ? "  ·  optional" : "");
}
