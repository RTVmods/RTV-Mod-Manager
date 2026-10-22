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

    /// <summary>True while mods dragged from the side panel hover over
    /// this card (a drop copies them into this profile).</summary>
    [ObservableProperty] private bool _isDropTarget;

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
public sealed partial class MatrixRowItem : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private readonly bool _loading;

    public MatrixRowItem(ProfilesViewModel owner, ProfileMatrixRow row, IReadOnlyList<MatrixCell> cells,
        IReadOnlyList<MatrixAction> actions, bool isChecked)
    {
        _owner = owner;
        Row = row;
        Cells = cells;
        Actions = actions;
        _loading = true;
        IsChecked = isChecked;
        _loading = false;
    }

    public ProfileMatrixRow Row { get; }
    public string Name => Row.DisplayName;
    public string ModId => Row.ModId;
    public string What => Row.What;
    public IReadOnlyList<MatrixCell> Cells { get; }
    public IReadOnlyList<MatrixAction> Actions { get; }
    public string MoreName => $"Copy or remove {Row.DisplayName}";
    public string CheckName => $"Select {Row.DisplayName}";

    /// <summary>Ticked for the toolbar's copy / remove actions.</summary>
    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_loading) _owner.OnRowChecked(this, value);
    }
}

/// <summary>Mods dragged from the side panel onto another profile's card.</summary>
public sealed record DraggedProfileMods(ModProfile Source, IReadOnlyList<ProfileMod> Mods);

/// <summary>One mod in the side panel's list of a profile.</summary>
public sealed partial class ProfileModItem : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private bool _loading;

    public ProfileModItem(ProfilesViewModel owner, ModProfile profile, ProfileModView view, bool isSelected)
    {
        _owner = owner;
        Profile = profile;
        View = view;
        _loading = true;
        Enabled = view.Mod.IsEnabled;
        PriorityText = view.Mod.Priority.ToString();
        IsSelected = isSelected;
        _loading = false;
    }

    public ModProfile Profile { get; }
    public ProfileModView View { get; }
    public ProfileMod Mod => View.Mod;
    public string Name => string.IsNullOrEmpty(Mod.DisplayName) ? Mod.ModId : Mod.DisplayName;
    public string Detail => string.Join(" · ", new[]
    {
        string.IsNullOrEmpty(Mod.Version) ? "" : Mod.Version,
        Mod.ModId,
    }.Where(s => s.Length > 0));
    public string Note => View.Note;
    public string RemoveName => $"Remove {Name} from {Profile.Name}";
    public string PriorityName => $"Load order of {Name}";

    [ObservableProperty] private bool _enabled;

    /// <summary>The load-order box's text; committed on Enter or when
    /// the box loses focus.</summary>
    [ObservableProperty] private string _priorityText = "";

    /// <summary>Selected in the side panel's list (two-way with the row).</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _owner.SetEnabled(this, value);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_loading) return;
        _owner.OnDetailSelectionChanged();
    }

    /// <summary>Saves the typed load order; anything that isn't a whole
    /// number puts the saved one back.</summary>
    public void CommitPriority()
    {
        var text = (PriorityText ?? "").Trim();
        if (int.TryParse(text, out var p) && p != Mod.Priority && _owner.SetPriority(this, p)) return;
        RevertPriority();
    }

    public void RevertPriority()
    {
        _loading = true;
        PriorityText = Mod.Priority.ToString();
        _loading = false;
    }

    /// <summary>Re-reads the tick and load order after the session reloaded.</summary>
    public void Sync()
    {
        _loading = true;
        Enabled = Mod.IsEnabled;
        PriorityText = Mod.Priority.ToString();
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
    /// <summary>The matrix rows ticked, by mod id (kept across filters
    /// and reloads while the mod is still in some profile).</summary>
    private readonly HashSet<string> _checkedIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _bulkChecking;

    public ProfilesViewModel(ManagerSession session) : base(session)
    {
        // The page shows the latest profile outcome itself (a switch's
        // copied / kept counts, a rename, a version move), not only the
        // title strip.
        session.Status += (area, text) =>
        {
            if (area == StatusArea.Profiles) Notice = text;
        };
    }

    public ObservableCollection<ProfileCard> Cards { get; } = new();
    public ObservableCollection<MatrixColumn> Columns { get; } = new();
    public ObservableCollection<MatrixRowItem> Rows { get; } = new();
    public ObservableCollection<ProfileModItem> DetailMods { get; } = new();

    /// <summary>The outcome of the last profile action taken on this page.</summary>
    [ObservableProperty] private string _notice = "";

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

    // side panel selection
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedModsCommand))]
    [NotifyPropertyChangedFor(nameof(HasDetailSelection))]
    private int _detailSelectedCount;
    public bool HasDetailSelection => DetailSelectedCount > 0;
    [ObservableProperty] private string _removeSelectedLabel = "Remove from profile";
    [ObservableProperty] private string _copySelectedLabel = "Copy to profile…";

    // matrix selection and its toolbar
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCheckedEntriesCommand), nameof(CopyCheckedVersionsCommand), nameof(RemoveCheckedEntriesCommand))]
    private int _checkedCount;
    [ObservableProperty] private string _checkedSummary = "";
    /// <summary>The header tick: true when every shown row is ticked,
    /// false when none is, null in between.</summary>
    [ObservableProperty] private bool? _allShownChecked = false;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCheckedEntriesCommand), nameof(CopyCheckedVersionsCommand))]
    private ProfileCard? _copyFrom;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCheckedEntriesCommand), nameof(CopyCheckedVersionsCommand))]
    private ProfileCard? _copyTo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCheckedEntriesCommand))]
    private ProfileCard? _removeFrom;

    /// <summary>The frozen mod column's width.</summary>
    public double NameWidth => NameColumnWidth;

    partial void OnOnlyDifferencesChanged(bool value) => FillRows();
    partial void OnQueryChanged(string value) => FillRows();
    partial void OnDetailQueryChanged(string value) => FillDetail(force: true);

    partial void OnAllShownCheckedChanged(bool? value)
    {
        if (_bulkChecking || value == null) return;
        _bulkChecking = true;
        foreach (var r in Rows) r.IsChecked = value.Value;
        _bulkChecking = false;
        UpdateChecked();
    }

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

        // The toolbar's picks survive the cards being rebuilt (clearing
        // them nulls the combo boxes' selection).
        var (fromName, toName, removeName) = (CopyFrom?.Name, CopyTo?.Name, RemoveFrom?.Name);
        Cards.Clear();
        foreach (var s in summaries)
            Cards.Add(new ProfileCard(this, s, IsSame(s.Name, _detailName)));
        CopyFrom = FindCard(fromName) ?? Cards.FirstOrDefault();
        CopyTo = FindCard(toName) is { } t && t != CopyFrom ? t : Cards.FirstOrDefault(c => c != CopyFrom);
        RemoveFrom = FindCard(removeName) ?? CopyTo;

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
        // A ticked mod no profile lists any more has nothing left to act on.
        var ids = new HashSet<string>(_comparison?.Rows.Select(r => r.ModId) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        _checkedIds.RemoveWhere(id => !ids.Contains(id));
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
            UpdateChecked();
            return;
        }
        var shown = _comparison.Filter(OnlyDifferences, Query).ToList();
        foreach (var r in shown) Rows.Add(BuildRow(r));
        var total = _comparison.Rows.Count;
        MatrixCount = $"{shown.Count} of {total} mods";
        MatrixEmpty = shown.Count > 0 ? ""
            : Query.Trim().Length > 0 ? $"No mod matches '{Query.Trim()}'."
            : OnlyDifferences ? "Every profile agrees on every mod." : "No mods in any profile.";
        UpdateChecked();
    }

    // ── matrix selection ──────────────────────────────────────────────

    public void OnRowChecked(MatrixRowItem row, bool value)
    {
        if (value) _checkedIds.Add(row.ModId);
        else _checkedIds.Remove(row.ModId);
        if (!_bulkChecking) UpdateChecked();
    }

    /// <summary>The ticked mod ids, in the comparison's row order (rows
    /// hidden by the filter included).</summary>
    private List<string> CheckedIds() =>
        _comparison == null ? new()
            : _comparison.Rows.Select(r => r.ModId).Where(_checkedIds.Contains).ToList();

    private void UpdateChecked()
    {
        CheckedCount = CheckedIds().Count;
        var shown = Rows.Count(r => r.IsChecked);
        var hidden = CheckedCount - shown;
        CheckedSummary = CheckedCount == 0
            ? "Tick mods to copy or remove several at once"
            : $"{CheckedCount} selected" + (hidden > 0 ? $" · {hidden} hidden by the filter" : "");
        _bulkChecking = true;
        AllShownChecked = shown == 0 ? false : shown == Rows.Count ? true : null;
        _bulkChecking = false;
    }

    private void ClearChecked()
    {
        _checkedIds.Clear();
        _bulkChecking = true;
        foreach (var r in Rows) r.IsChecked = false;
        _bulkChecking = false;
        UpdateChecked();
    }

    private bool CanCopyChecked() =>
        CheckedCount > 0 && CopyFrom != null && CopyTo != null && CopyFrom != CopyTo;

    private bool CanRemoveChecked() => CheckedCount > 0 && RemoveFrom != null;

    /// <summary>Copies the ticked rows' whole entries (version, enabled
    /// state, load order) from one profile into another.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyChecked))]
    private void CopyCheckedEntries()
    {
        if (CopyFrom == null || CopyTo == null) return;
        var (from, to) = (CopyFrom.Profile, CopyTo.Profile);
        var n = Session.CopyProfileEntries(from, to, CheckedIds());
        if (n == 0) return;  // the session said why
        Notice = $"Copied {n} entr{(n == 1 ? "y" : "ies")} from '{from.Name}' to '{to.Name}'.";
        ClearChecked();
        Load();
    }

    /// <summary>Moves the ticked rows in the target profile to the
    /// source profile's versions, leaving on/off and load order alone.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyChecked))]
    private void CopyCheckedVersions()
    {
        if (CopyFrom == null || CopyTo == null) return;
        var (from, to) = (CopyFrom.Profile, CopyTo.Profile);
        var n = Session.CopyProfileVersions(from, to, CheckedIds());
        if (n == 0)
        {
            Session.Prompt.Inform("No versions changed",
                $"None of the selected mods needed a change: each one is either missing from '{from.Name}' "
                + $"or '{to.Name}', or already at the same version in both.");
            return;
        }
        // The session's status line ("Moved n mod(s) ...") lands in Notice.
        ClearChecked();
        Load();
    }

    /// <summary>Drops the ticked rows from one profile (after the
    /// session's confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveChecked))]
    private void RemoveCheckedEntries()
    {
        if (RemoveFrom == null) return;
        var profile = RemoveFrom.Profile;
        var ids = CheckedIds();
        var present = ids.Count(id => profile.Mods.Any(m => IsSame(m.ModId, id)));
        if (present == 0)
        {
            Session.Prompt.Inform("Nothing to remove", $"None of the selected mods are in '{profile.Name}'.");
            return;
        }
        if (!Session.RemoveProfileEntries(profile, ids)) return;
        Notice = $"Removed {present} mod{(present == 1 ? "" : "s")} from '{profile.Name}'.";
        ClearChecked();
        Load();
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
        return new MatrixRowItem(this, row, cells, actions, _checkedIds.Contains(row.ModId));
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
            // The selection follows the mods (by entry, or by id when
            // the profile was re-read from disk) across a rebuild.
            var selected = new HashSet<string>(DetailMods.Where(i => i.IsSelected).Select(i => i.Mod.ModId),
                StringComparer.OrdinalIgnoreCase);
            DetailMods.Clear();
            foreach (var v in views)
                DetailMods.Add(new ProfileModItem(this, profile, v, !force && selected.Contains(v.Mod.ModId)));
            _detailMods = mods;
        }
        var on = profile.Mods.Count(m => m.IsEnabled);
        DetailSummary = $"{profile.Mods.Count} mods · {on} enabled"
            + (Detail.IsActive ? "" : " · edits here change the profile only, not the live mods folder");
        OnDetailSelectionChanged();
    }

    public void SetEnabled(ProfileModItem item, bool enabled)
        => Session.SetProfileModEnabled(item.Profile, item.Mod, enabled);

    /// <summary>Saves a row's typed load order. False when it wasn't saved.</summary>
    public bool SetPriority(ProfileModItem item, int priority)
    {
        // A row a reload already replaced has a stale entry: ignore it.
        if (!DetailMods.Contains(item)) return false;
        var name = item.Name;
        if (!Session.SetProfileModPriority(item.Profile, item.Mod, priority)) return false;
        Notice = $"'{name}' now loads at priority {priority} in '{item.Profile.Name}'.";
        return true;
    }

    public void RemoveFromProfile(ProfileModItem item)
    {
        if (!Session.RemoveModsFromProfile(item.Profile, new[] { item.Mod })) return;
        Notice = $"Removed '{item.Name}' from '{item.Profile.Name}'.";
        Load();
    }

    /// <summary>The side panel's selected rows, in list order.</summary>
    public List<ProfileModItem> DetailSelection => DetailMods.Where(i => i.IsSelected).ToList();

    /// <summary>The profiles the selected rows can be copied into.</summary>
    public List<ProfileCard> CopyTargets => Cards.Where(c => c != Detail).ToList();

    public void OnDetailSelectionChanged()
    {
        var n = DetailMods.Count(i => i.IsSelected);
        DetailSelectedCount = n;
        RemoveSelectedLabel = n > 1 ? $"Remove from profile ({n})" : "Remove from profile";
        CopySelectedLabel = n > 1 ? $"Copy {n} to profile…" : "Copy to profile…";
    }

    private bool CanRemoveSelectedMods() => DetailSelectedCount > 0;

    /// <summary>Removes every selected row from the open profile, after
    /// one confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedMods))]
    private void RemoveSelectedMods()
    {
        if (Detail == null) return;
        var profile = Detail.Profile;
        var mods = DetailSelection.Select(i => i.Mod).ToList();
        if (!Session.RemoveModsFromProfile(profile, mods)) return;
        Notice = $"Removed {mods.Count} mod{(mods.Count == 1 ? "" : "s")} from '{profile.Name}'.";
        Load();
    }

    /// <summary>The drag payload for the selected rows, or null.</summary>
    public DraggedProfileMods? DragPayload()
    {
        if (Detail == null) return null;
        var mods = DetailSelection.Select(i => i.Mod).ToList();
        return mods.Count == 0 ? null : new DraggedProfileMods(Detail.Profile, mods);
    }

    /// <summary>"Copy to profile…": the selected rows into `target`.</summary>
    public void CopySelectedTo(ProfileCard target)
    {
        if (DragPayload() is { } payload) CopyMods(target, payload);
    }

    /// <summary>A drop of side-panel rows on a card, or "Copy to
    /// profile…": new metadata-only entries in `target`, ids it already
    /// lists skipped. The session tells the user the counts; the page
    /// keeps them too.</summary>
    public void CopyMods(ProfileCard target, DraggedProfileMods payload)
    {
        if (ReferenceEquals(target.Profile, payload.Source)) return;
        var name = target.Name;
        var result = Session.CopyModsToProfile(target.Profile, payload.Mods, payload.Source);
        Notice = result.Added > 0
            ? $"Copied {result.Added} mod{(result.Added == 1 ? "" : "s")} from '{payload.Source.Name}' into '{name}'"
              + (result.Skipped > 0 ? $" · {result.Skipped} already there, skipped." : ".")
            : $"Nothing copied into '{name}': "
              + (result.Skipped > 0 ? $"all {result.Skipped} already there." : "no mods picked.");
        Load();
    }

    /// <summary>Whether a drag of `payload` can land on `card`.</summary>
    public static bool CanDrop(ProfileCard card, DraggedProfileMods payload)
        => !ReferenceEquals(card.Profile, payload.Source);

    // ── card actions ──────────────────────────────────────────────────

    /// <summary>Switches the live mods folder to `card`'s profile. The
    /// page reloads whatever happened: a failed switch can leave part of
    /// it done, and a cancelled one may still have saved version picks.</summary>
    public void MakeActive(ProfileCard card)
    {
        var name = card.Name;
        var before = Session.ActiveProfile?.Name;
        var ok = Session.SwitchActiveProfile(card.Profile);
        Load();
        // On success the session's own status line ("Switched to ... ·
        // n copied · n locked kept") is already in Notice.
        if (!ok)
            Notice = $"'{name}' was not made active"
                + (before != null ? $"; '{before}' is still the active profile." : ".");
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

    private ProfileCard? FindCard(string? name) =>
        name == null ? null : Cards.FirstOrDefault(c => IsSame(c.Name, name));

    private static bool IsSame(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
