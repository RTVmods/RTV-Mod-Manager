using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One saved profile's card.</summary>
public sealed partial class ProfileCard : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public ProfileCard(ProfilesViewModel owner, ProfileSummary summary, bool isOpen)
    {
        _owner = owner;
        Summary = summary;
        _isOpen = isOpen;
    }

    public ProfileSummary Summary { get; }
    public ModProfile Profile => Summary.Profile;
    public string Name => Summary.Name;
    public bool IsActive => Summary.IsActive;
    public string Tag => IsActive ? "ACTIVE" : "";

    /// <summary>The description, or what the profile is when it has none.</summary>
    public string Role
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Summary.Description)) return Summary.Description.Trim();
            if (IsActive) return "What the mods folder runs now.";
            var when = (Summary.UpdatedAt ?? Summary.CreatedAt).ToLocalTime();
            return $"Saved profile · last changed {when:d MMM yyyy}";
        }
    }

    public string ModCount => Summary.ModCount.ToString();
    public string EnabledCount => Summary.EnabledCount.ToString();

    /// <summary>The Save Slots slots tied to this profile, or a dash.</summary>
    public string Slots => Summary.SaveSlots.Count == 0
        ? "No save slots tied"
        : string.Join(" · ", Summary.SaveSlots.Select(s => s.Occupied ? $"{s.Number} {s.Title}" : $"{s.Number} (empty)"));
    public bool HasSlots => Summary.SaveSlots.Count > 0;

    public string MainLabel => IsActive ? "Open mods" : "Make active";

    /// <summary>True while this card's mod list is open in the side panel.</summary>
    [ObservableProperty] private bool _isOpen;

    [RelayCommand] private void Main() { if (IsActive) _owner.OpenModsPage(); else _owner.MakeActive(this); }
    [RelayCommand] private void Clone() => _owner.Clone(this);
    [RelayCommand] private void Export() => _owner.Export(this);
    [RelayCommand] private void Rename() => _owner.Rename(this);
    [RelayCommand] private void Describe() => _owner.Describe(this);
    [RelayCommand] private void Delete() => _owner.Delete(this);
    [RelayCommand] private Task Apply() => _owner.ApplyAsync(Profile);
    [RelayCommand] private void ShowMods() => _owner.ToggleDetail(this);
}

/// <summary>A profile's column in the comparison matrix.</summary>
public sealed record MatrixColumn(string Name, bool IsActive)
{
    public string Tag => IsActive ? "ACTIVE" : "";
}

/// <summary>How a matrix cell reads against the rest of its row.</summary>
public enum CellTone { Same, Differs, Absent }

/// <summary>One profile's entry for one mod, as the matrix shows it.</summary>
public sealed record MatrixCell(string Text, CellTone Tone, string Profile)
{
    public string Tooltip => $"{Profile}: {Text}";
}

/// <summary>One entry of a row's ⋯ menu: a heading (no command), a
/// separator (empty header) or an action.</summary>
public sealed record MatrixAction(string Header, ICommand? Command)
{
    public bool IsSeparator => Header.Length == 0;
    public static MatrixAction Separator { get; } = new("", null);
}

/// <summary>One mod across every saved profile.</summary>
public sealed class MatrixRowItem
{
    public MatrixRowItem(ProfileMatrixRow row, IReadOnlyList<MatrixCell> cells, IReadOnlyList<MatrixAction> actions)
    {
        Row = row;
        Cells = cells;
        Actions = actions;
    }

    public ProfileMatrixRow Row { get; }
    public string Name => Row.DisplayName;
    public string ModId => Row.ModId;
    public string What => Row.What;
    public IReadOnlyList<MatrixCell> Cells { get; }
    public IReadOnlyList<MatrixAction> Actions { get; }
    public string MoreName => $"Copy or remove {Row.DisplayName}";
}

/// <summary>One mod in the side panel's list of a profile.</summary>
public sealed partial class ProfileModItem : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private bool _loading;

    public ProfileModItem(ProfilesViewModel owner, ModProfile profile, ProfileModView view)
    {
        _owner = owner;
        Profile = profile;
        View = view;
        _loading = true;
        Enabled = view.Mod.IsEnabled;
        _loading = false;
    }

    public ModProfile Profile { get; }
    public ProfileModView View { get; }
    public ProfileMod Mod => View.Mod;
    public string Name => string.IsNullOrEmpty(Mod.DisplayName) ? Mod.ModId : Mod.DisplayName;
    public string Detail => string.Join(" · ", new[]
    {
        string.IsNullOrEmpty(Mod.Version) ? "" : Mod.Version,
        $"prio {Mod.Priority}",
        Mod.ModId,
    }.Where(s => s.Length > 0));
    public string Note => View.Note;
    public string RemoveName => $"Remove {Name} from {Profile.Name}";

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _owner.SetEnabled(this, value);
    }

    /// <summary>Re-reads the tick after the session reloaded.</summary>
    public void Sync()
    {
        _loading = true;
        Enabled = Mod.IsEnabled;
        _loading = false;
    }

    [RelayCommand] private void Remove() => _owner.RemoveFromProfile(this);
}

/// <summary>The Profiles page: a card per saved profile, the comparison
/// matrix across all of them, and a side panel with one profile's mods.</summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private const double NameColumnWidth = 300;
    private const double MinCellWidth = 118;

    private ProfileComparison? _comparison;
    private bool _loaded;
    private string? _detailName;
    private List<ProfileMod> _detailMods = new();

    public ProfilesViewModel(ManagerSession session) : base(session) { }

    public ObservableCollection<ProfileCard> Cards { get; } = new();
    public ObservableCollection<MatrixColumn> Columns { get; } = new();
    public ObservableCollection<MatrixRowItem> Rows { get; } = new();
    public ObservableCollection<ProfileModItem> DetailMods { get; } = new();

    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _matrixSummary = "";
    [ObservableProperty] private string _matrixCount = "";
    [ObservableProperty] private string _matrixEmpty = "";
    [ObservableProperty] private bool _onlyDifferences = true;
    [ObservableProperty] private string _query = "";
    /// <summary>Below this the cells scroll sideways instead of squeezing.</summary>
    [ObservableProperty] private double _cellsMinWidth;
    [ObservableProperty] private bool _canCompare;

    [ObservableProperty] private ProfileCard? _detail;
    [ObservableProperty] private string _detailQuery = "";
    [ObservableProperty] private string _detailSummary = "";

    /// <summary>The frozen mod column's width.</summary>
    public double NameWidth => NameColumnWidth;

    partial void OnOnlyDifferencesChanged(bool value) => FillRows();
    partial void OnQueryChanged(string value) => FillRows();
    partial void OnDetailQueryChanged(string value) => FillDetail(force: true);

    public override void OnShown() => Load();

    public override void Reload()
    {
        // Only re-read while the page has been opened once.
        if (_loaded) Load();
    }

    // ── loading ───────────────────────────────────────────────────────

    private void Load()
    {
        _loaded = true;
        // The active profile leads: first card, first matrix column (the
        // comparison sorts "only in the first profile" to the top).
        var summaries = Session.GetProfileSummaries().OrderByDescending(s => s.IsActive).ToList();

        Cards.Clear();
        foreach (var s in summaries)
            Cards.Add(new ProfileCard(this, s, IsSame(s.Name, _detailName)));

        var active = summaries.FirstOrDefault(s => s.IsActive);
        Header = summaries.Count switch
        {
            0 => "No saved profiles yet. A profile is a named set of mods, versions and load order you can switch between.",
            _ => $"{summaries.Count} saved profile{(summaries.Count == 1 ? "" : "s")}"
                 + (active != null ? $" · '{active.Name}' is live in the mods folder" : " · none is active")
                 + ". Switching swaps the mods folder from the Library; locked mods stay put.",
        };

        var profiles = summaries.Select(s => s.Profile).ToList();
        Columns.Clear();
        foreach (var s in summaries) Columns.Add(new MatrixColumn(s.Name, s.IsActive));
        CanCompare = profiles.Count >= 2;
        _comparison = profiles.Count >= 2 ? Session.CompareProfiles(profiles) : null;
        CellsMinWidth = Math.Max(1, profiles.Count) * MinCellWidth;
        MatrixSummary = _comparison?.Summary
            ?? (profiles.Count == 1 ? "Save a second profile to compare them here." : "");
        FillRows();

        // Keep the side panel on the same profile across reloads.
        var open = Cards.FirstOrDefault(c => c.IsOpen);
        Detail = open;
        if (open == null) _detailName = null;
        FillDetail(force: false);
    }

    private void FillRows()
    {
        Rows.Clear();
        if (_comparison == null)
        {
            MatrixCount = "";
            MatrixEmpty = "";
            return;
        }
        var shown = _comparison.Filter(OnlyDifferences, Query).ToList();
        foreach (var r in shown) Rows.Add(BuildRow(r));
        var total = _comparison.Rows.Count;
        MatrixCount = $"{shown.Count} of {total} mods";
        MatrixEmpty = shown.Count > 0 ? ""
            : Query.Trim().Length > 0 ? $"No mod matches '{Query.Trim()}'."
            : OnlyDifferences ? "Every profile agrees on every mod." : "No mods in any profile.";
    }

    /// <summary>A matrix row: each cell's text and tone, and the row's menu.</summary>
    private MatrixRowItem BuildRow(ProfileMatrixRow row)
    {
        var profiles = _comparison!.Profiles;
        var present = row.Cells.Where(c => c.Present).ToList();
        var priorityMatters = present.Select(c => c.Priority).Distinct().Count() > 1;

        string Sig(ProfileMatrixCell c) => c.Present ? $"{c.Version}\n{c.Enabled}\n{c.Priority}" : "";
        string Text(ProfileMatrixCell c)
        {
            if (!c.Present) return "not in profile";
            var bits = new List<string> { string.IsNullOrEmpty(c.Version) ? "no version" : c.Version };
            if (!c.Enabled) bits.Add("off");
            if (priorityMatters) bits.Add($"prio {c.Priority}");
            return string.Join(" · ", bits);
        }

        // The most common entry is the reference; the active profile's
        // entry wins a tie, so "differs" reads as "differs from live".
        var activeIndex = IndexOfActive(profiles);
        var groups = row.Cells
            .Select((c, i) => (Cell: c, Index: i))
            .Where(x => x.Cell.Present)
            .GroupBy(x => Sig(x.Cell))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Any(x => x.Index == activeIndex))
            .ToList();
        var reference = groups.Count > 0 ? groups[0].Key : "";

        var cells = row.Cells.Select((c, i) => new MatrixCell(
                Text(c),
                !c.Present ? CellTone.Absent : Sig(c) == reference ? CellTone.Same : CellTone.Differs,
                profiles[i].Name))
            .ToList();

        // The menu: the common entry can be copied into every profile
        // that holds something else (or nothing); a less common one only
        // into the active profile. Then one remove per profile.
        var actions = new List<MatrixAction>();
        foreach (var g in groups)
        {
            var holders = g.Select(x => x.Index).ToList();
            var sourceIndex = holders.Contains(activeIndex) ? activeIndex : holders[0];
            var source = profiles[sourceIndex];
            var targets = Enumerable.Range(0, profiles.Count)
                .Where(i => !holders.Contains(i) && (g.Key == reference || i == activeIndex))
                .ToList();
            if (targets.Count == 0) continue;
            if (actions.Count > 0) actions.Add(MatrixAction.Separator);
            actions.Add(new MatrixAction($"Copy {Text(row.Cells[sourceIndex])} from '{source.Name}' to", null));
            foreach (var t in targets)
            {
                var target = profiles[t];
                actions.Add(new MatrixAction($"    {target.Name}",
                    new RelayCommand(() => CopyEntry(source, target, row.ModId))));
            }
        }
        var removable = row.Cells.Select((c, i) => (c, i)).Where(x => x.c.Present).Select(x => profiles[x.i]).ToList();
        if (removable.Count > 0)
        {
            if (actions.Count > 0) actions.Add(MatrixAction.Separator);
            actions.Add(new MatrixAction("Remove it from", null));
            foreach (var p in removable)
                actions.Add(new MatrixAction($"    {p.Name}…", new RelayCommand(() => RemoveEntry(p, row.ModId))));
        }
        return new MatrixRowItem(row, cells, actions);
    }

    private int IndexOfActive(IReadOnlyList<ModProfile> profiles)
    {
        for (var i = 0; i < profiles.Count; i++)
            if (Session.IsActiveProfile(profiles[i])) return i;
        return -1;
    }

    // ── side panel: one profile's mods ────────────────────────────────

    public void ToggleDetail(ProfileCard card)
    {
        var open = !card.IsOpen;
        foreach (var c in Cards) c.IsOpen = false;
        card.IsOpen = open;
        _detailName = open ? card.Name : null;
        Detail = open ? card : null;
        DetailQuery = "";
        FillDetail(force: true);
    }

    [RelayCommand]
    private void CloseDetail()
    {
        foreach (var c in Cards) c.IsOpen = false;
        _detailName = null;
        Detail = null;
        FillDetail(force: true);
    }

    /// <summary>Fills the side panel's list. A reload that left the same
    /// mods in place only re-reads the ticks, so a tick being clicked
    /// keeps its row (and focus).</summary>
    private void FillDetail(bool force)
    {
        if (Detail == null)
        {
            DetailMods.Clear();
            _detailMods.Clear();
            DetailSummary = "";
            return;
        }
        var profile = Detail.Profile;
        var views = Session.GetProfileMods(profile, DetailQuery);
        var mods = views.Select(v => v.Mod).ToList();
        if (!force && mods.Count == _detailMods.Count && mods.Zip(_detailMods).All(p => ReferenceEquals(p.First, p.Second)))
        {
            foreach (var item in DetailMods) item.Sync();
        }
        else
        {
            DetailMods.Clear();
            foreach (var v in views) DetailMods.Add(new ProfileModItem(this, profile, v));
            _detailMods = mods;
        }
        var on = profile.Mods.Count(m => m.IsEnabled);
        DetailSummary = $"{profile.Mods.Count} mods · {on} enabled"
            + (Detail.IsActive ? "" : " · edits here change the profile only, not the live mods folder");
    }

    public void SetEnabled(ProfileModItem item, bool enabled)
        => Session.SetProfileModEnabled(item.Profile, item.Mod, enabled);

    public void RemoveFromProfile(ProfileModItem item)
    {
        if (Session.RemoveModsFromProfile(item.Profile, new[] { item.Mod })) Load();
    }

    // ── card actions ──────────────────────────────────────────────────

    public void MakeActive(ProfileCard card)
    {
        if (Session.SwitchActiveProfile(card.Profile)) Load();
    }

    public void OpenModsPage()
    {
        if (Application.Current?.MainWindow?.DataContext is ShellViewModel shell) shell.Open("mods");
    }

    public void Clone(ProfileCard card)
    {
        if (Session.CloneProfile(card.Profile) != null) Load();
    }

    public void Rename(ProfileCard card)
    {
        var wasOpen = card.IsOpen;
        if (!Session.RenameProfile(card.Profile)) return;
        if (wasOpen) _detailName = card.Profile.Name;
        Load();
    }

    public void Describe(ProfileCard card)
    {
        if (Session.SetProfileDescription(card.Profile)) Load();
    }

    public void Delete(ProfileCard card)
    {
        if (!Session.DeleteProfile(card.Profile)) return;
        if (card.IsOpen) _detailName = null;
        Load();
    }

    public void Export(ProfileCard card)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Export '{card.Name}'",
            FileName = Path.GetFileNameWithoutExtension(ManagerSession.DefaultExportFileName(card.Profile)),
            Filter = "Profile with its mods (*.vmprofile)|*.vmprofile"
                   + "|Mod Loader modpack, list only (*.zip)|*.zip"
                   + "|Mod list, no files (*.json)|*.json",
            FilterIndex = 1,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (!ShowFileDialog(dialog)) return;
        // The extension picks the format (.vmprofile / .zip / .json); the
        // filter the user chose sets the extension.
        Session.ExportProfile(card.Profile, dialog.FileName);
    }

    private static bool ShowFileDialog(CommonDialog dialog)
    {
        var owner = Application.Current?.MainWindow;
        return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
    }

    /// <summary>Opens the Apply plan for `profile`, runs it if asked,
    /// then checks for updates as a switch would.</summary>
    public async Task ApplyAsync(ModProfile profile)
    {
        ApplyPlan plan;
        try { plan = Session.BuildApplyPlan(profile); }
        catch (Exception ex)
        {
            Session.Prompt.Error("Couldn't plan the apply", ex);
            return;
        }
        var vm = new ApplyPlanViewModel(Session, plan);
        new ApplyPlanWindow(vm) { Owner = Application.Current?.MainWindow }.ShowDialog();
        if (!vm.Finished) return;  // closed without running
        Load();
        if (vm.Result is { Applied: true }) await Session.CheckUpdatesAsync();
    }

    // ── header actions ────────────────────────────────────────────────

    [RelayCommand]
    private void NewProfile()
    {
        if (Session.CreateNewModlistProfile() != null) Load();
    }

    [RelayCommand]
    private async Task Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a profile",
            Filter = "Profiles (*.vmprofile;*.zip;*.json)|*.vmprofile;*.zip;*.json|All files (*.*)|*.*",
            Multiselect = false,
        };
        if (!ShowFileDialog(dialog)) return;
        ProfileImportResult? result;
        try { result = Session.ImportProfileFile(dialog.FileName); }
        catch (Exception ex)
        {
            Session.Prompt.Error("Import failed", ex);
            return;
        }
        Load();
        if (result is { ApplyRequested: true }) await ApplyAsync(result.Profile);
    }

    [RelayCommand]
    private void ShowDependencies()
    {
        ProfileDependencyReport report;
        try { report = Session.GetProfileDependencies(); }
        catch (Exception ex)
        {
            Session.Prompt.Error("Couldn't read the dependencies", ex);
            return;
        }
        new ProfileDependenciesWindow(report) { Owner = Application.Current?.MainWindow }.ShowDialog();
    }

    // ── matrix actions ────────────────────────────────────────────────

    private void CopyEntry(ModProfile from, ModProfile to, string modId)
    {
        if (Session.CopyProfileEntries(from, to, new[] { modId }) > 0) Load();
    }

    private void RemoveEntry(ModProfile profile, string modId)
    {
        if (Session.RemoveProfileEntries(profile, new[] { modId })) Load();
    }

    private static bool IsSame(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
