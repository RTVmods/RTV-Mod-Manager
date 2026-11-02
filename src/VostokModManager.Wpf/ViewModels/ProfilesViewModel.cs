using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One saved profile in the page's list.</summary>
public sealed partial class ProfileCard : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public ProfileCard(ProfilesViewModel owner, ProfileSummary summary)
    {
        _owner = owner;
        Summary = summary;
    }

    public ProfileSummary Summary { get; }
    public ModProfile Profile => Summary.Profile;
    public string Name => Summary.Name;
    public bool IsActive => Summary.IsActive;

    /// <summary>The description, or what the profile is when it has none.</summary>
    public string Role
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Summary.Description)) return Summary.Description.Trim();
            if (IsActive) return "Your loadout: what the game loads.";
            var when = (Summary.UpdatedAt ?? Summary.CreatedAt).ToLocalTime();
            return $"Saved profile · last changed {when:d MMM yyyy}";
        }
    }

    public string Counts => $"{Summary.ModCount} mods · {Summary.EnabledCount} on";

    /// <summary>The Save Slots slots tied to this profile, or "".</summary>
    public string Slots => Summary.SaveSlots.Count == 0 ? ""
        : "Slot " + string.Join(" · ", Summary.SaveSlots.Select(s => s.Occupied ? $"{s.Number} {s.Title}" : $"{s.Number} (empty)"));
    public bool HasSlots => Summary.SaveSlots.Count > 0;

    public string MainLabel => IsActive ? "Open loadout" : "Make active";

    /// <summary>The profile the selected one is being compared with.</summary>
    [ObservableProperty] private bool _isCompared;

    /// <summary>True while mods dragged from the mod list hover over this
    /// profile (a drop copies them into it).</summary>
    [ObservableProperty] private bool _isDropTarget;

    [RelayCommand] private void Main() { if (IsActive) _owner.OpenModsPage(); else _owner.MakeActive(this); }
    [RelayCommand] private void Clone() => _owner.Clone(this);
    [RelayCommand] private void Export() => _owner.Export(this);
    [RelayCommand] private void Rename() => _owner.Rename(this);
    [RelayCommand] private void Describe() => _owner.Describe(this);
    [RelayCommand] private void Delete() => _owner.Delete(this);
    [RelayCommand] private Task Apply() => _owner.ApplyAsync(Profile);
    [RelayCommand] private void CompareHere() => _owner.CompareWith = this;
}

/// <summary>Mods dragged from the mod list onto another profile.</summary>
public sealed record DraggedProfileMods(ModProfile Source, IReadOnlyList<ProfileMod> Mods);

/// <summary>One mod in the selected profile's list.</summary>
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

    /// <summary>Selected in the list (two-way with the row).</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _owner.SetEnabled(this, value);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_loading) return;
        _owner.OnModSelectionChanged();
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

/// <summary>One mod in the A/B comparison: each side's entry as text,
/// and the copy (and remove) actions that apply to it.</summary>
public sealed partial class PairItem : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public PairItem(ProfilesViewModel owner, PairRow row, string aText, string bText)
    {
        _owner = owner;
        Row = row;
        AText = aText;
        BText = bText;
    }

    public PairRow Row { get; }
    public string Name => Row.DisplayName;
    public string ModId => Row.ModId;
    public string AText { get; }
    public string BText { get; }
    public bool HasA => Row.A != null;
    public bool HasB => Row.B != null;
    public bool IsDifferent => Row.Kind == PairKind.Different;
    public bool CanCopyToB => HasA && Row.Kind != PairKind.Same;
    public bool CanCopyToA => HasB && Row.Kind != PairKind.Same;
    public bool CanRemoveFromA => Row.Kind == PairKind.OnlyInA;
    public bool CanRemoveFromB => Row.Kind == PairKind.OnlyInB;
    public string CopyToBTip => $"Copy {Name} as it is in '{_owner.AName}' into '{_owner.BName}'";
    public string CopyToATip => $"Copy {Name} as it is in '{_owner.BName}' into '{_owner.AName}'";
    public string RemoveFromATip => $"Remove {Name} from '{_owner.AName}'";
    public string RemoveFromBTip => $"Remove {Name} from '{_owner.BName}'";

    [RelayCommand] private Task CopyToB() => _owner.CopyPairs(new[] { this }, toB: true);
    [RelayCommand] private Task CopyToA() => _owner.CopyPairs(new[] { this }, toB: false);
    [RelayCommand] private void RemoveFromA() => _owner.RemovePair(this, fromB: false);
    [RelayCommand] private void RemoveFromB() => _owner.RemovePair(this, fromB: true);
}

/// <summary>A group of the comparison: only in A, only in B, different,
/// the same. Each copies its whole group across in one click.</summary>
public sealed partial class PairSection : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public PairSection(ProfilesViewModel owner, PairKind kind, string title, string hint, IReadOnlyList<PairItem> items, bool expanded)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        Hint = hint;
        Items = items;
        _isExpanded = expanded;
    }

    public PairKind Kind { get; }
    public string Title { get; }
    public string Hint { get; }
    public IReadOnlyList<PairItem> Items { get; }
    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
    public bool CanCopyAllToB => Kind is PairKind.OnlyInA or PairKind.Different && HasItems;
    public bool CanCopyAllToA => Kind is PairKind.OnlyInB or PairKind.Different && HasItems;
    public string CopyAllToBLabel => Kind == PairKind.Different ? $"Use all of '{_owner.AName}' →" : $"Copy all {Count} to '{_owner.BName}' →";
    public string CopyAllToALabel => Kind == PairKind.Different ? $"← Use all of '{_owner.BName}'" : $"← Copy all {Count} to '{_owner.AName}'";

    /// <summary>"The same in both" starts folded; the others open.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToggleLabel))] private bool _isExpanded;
    public string ToggleLabel => IsExpanded ? "Hide" : "Show";

    [RelayCommand] private void Toggle() => IsExpanded = !IsExpanded;
    [RelayCommand] private Task CopyAllToB() => _owner.CopyPairs(Items, toB: true);
    [RelayCommand] private Task CopyAllToA() => _owner.CopyPairs(Items, toB: false);
}

/// <summary>The Profiles page: every saved profile down the left; the
/// selected one's mods on the right, or, with "Compare with", what
/// differs between it (A) and another profile (B), grouped so the mods
/// only one of them has stand out, each copyable across.</summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private bool _loaded;
    private string? _selectedName;
    private string? _compareName;
    private List<ProfileMod> _shownMods = new();
    private bool _reloading;

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
    /// <summary>What "Compare with" offers: every profile but the selected one.</summary>
    public ObservableCollection<ProfileCard> CompareChoices { get; } = new();
    public ObservableCollection<ProfileModItem> Mods { get; } = new();
    public ObservableCollection<PairSection> Sections { get; } = new();

    /// <summary>The outcome of the last profile action taken on this page.</summary>
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _modsSummary = "";
    [ObservableProperty] private string _compareSummary = "";
    [ObservableProperty] private string _compareEmpty = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedIsActive), nameof(SelectedEditable), nameof(HasSelected), nameof(AName))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedModsCommand), nameof(SwapCommand))]
    private ProfileCard? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing), nameof(NotComparing), nameof(BName))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand), nameof(StopComparingCommand))]
    private ProfileCard? _compareWith;

    public bool HasSelected => Selected != null;
    public bool IsComparing => Selected != null && CompareWith != null;
    public bool NotComparing => !IsComparing;
    public string AName => Selected?.Name ?? "";
    public string BName => CompareWith?.Name ?? "";

    /// <summary>The selected profile is the loadout: edited on the
    /// Loadout page, so here its list is read-only (its mods can still be
    /// copied to another profile).</summary>
    public bool SelectedIsActive => Selected?.IsActive == true;
    public bool SelectedEditable => Selected != null && !Selected.IsActive;

    [RelayCommand] private void EditInLoadout() => OpenModsPage();

    // the mod list's selection
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedModsCommand))]
    [NotifyPropertyChangedFor(nameof(HasModSelection))]
    private int _modSelectedCount;
    public bool HasModSelection => ModSelectedCount > 0;
    [ObservableProperty] private string _removeSelectedLabel = "Remove from profile";
    [ObservableProperty] private string _copySelectedLabel = "Copy to profile…";

    partial void OnQueryChanged(string value) => Fill(force: true);

    partial void OnSelectedChanged(ProfileCard? value)
    {
        if (_reloading) return;
        _selectedName = value?.Name;
        // A profile isn't compared with itself.
        if (value != null && CompareWith == value) CompareWith = null;
        FillCompareChoices();
        Fill(force: true);
    }

    partial void OnCompareWithChanged(ProfileCard? value)
    {
        if (_reloading) return;
        _compareName = value?.Name;
        foreach (var c in Cards) c.IsCompared = c == value;
        Fill(force: true);
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
        // The active profile leads the list.
        var summaries = Session.GetProfileSummaries().OrderByDescending(s => s.IsActive).ToList();

        _reloading = true;
        try
        {
            Cards.Clear();
            foreach (var s in summaries) Cards.Add(new ProfileCard(this, s));
            Selected = FindCard(_selectedName) ?? Cards.FirstOrDefault(c => c.IsActive) ?? Cards.FirstOrDefault();
            _selectedName = Selected?.Name;
            CompareWith = FindCard(_compareName) is { } b && b != Selected ? b : null;
            _compareName = CompareWith?.Name;
            foreach (var c in Cards) c.IsCompared = c == CompareWith;
            FillCompareChoices();
        }
        finally { _reloading = false; }

        var active = summaries.FirstOrDefault(s => s.IsActive);
        Header = summaries.Count switch
        {
            0 => "No saved profiles yet. A profile is a named set of mods, versions and load order you can switch between.",
            _ => $"{summaries.Count} saved profile{(summaries.Count == 1 ? "" : "s")}"
                 + (active != null ? $" · '{active.Name}' is your loadout" : " · none is active")
                 + ". Pick one to see its mods, or compare it with another.",
        };
        Fill(force: false);
    }

    private void FillCompareChoices()
    {
        var keep = CompareWith;
        CompareChoices.Clear();
        foreach (var c in Cards.Where(c => c != Selected)) CompareChoices.Add(c);
        // Clearing the list drops the combo's pick; put it back.
        if (keep != null && CompareChoices.Contains(keep) && CompareWith != keep) CompareWith = keep;
    }

    /// <summary>Fills whichever view is showing: the comparison, or the
    /// selected profile's mods.</summary>
    private void Fill(bool force)
    {
        if (IsComparing) FillComparison();
        else
        {
            Sections.Clear();
            CompareSummary = "";
            CompareEmpty = "";
        }
        FillMods(force || IsComparing);
    }

    // ── the comparison ────────────────────────────────────────────────

    private void FillComparison()
    {
        Sections.Clear();
        var (a, b) = (Selected!, CompareWith!);
        var pairs = Session.CompareProfiles(new[] { a.Profile, b.Profile }).Pairs();
        var q = Query.Trim();
        var shown = q.Length == 0 ? pairs
            : pairs.Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || p.ModId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        // Load order and on/off only show where they differ in that row.
        string Text(ProfileMod? m, ProfileMod? other)
        {
            if (m == null) return "—";
            var bits = new List<string> { string.IsNullOrEmpty(m.Version) ? "no version" : m.Version };
            if (!m.IsEnabled) bits.Add("off");
            if (other != null && other.Priority != m.Priority) bits.Add($"order {m.Priority}");
            return string.Join(" · ", bits);
        }
        List<PairItem> Of(PairKind kind) => shown.Where(p => p.Kind == kind)
            .Select(p => new PairItem(this, p, Text(p.A, p.B), Text(p.B, p.A))).ToList();

        Sections.Add(new PairSection(this, PairKind.OnlyInA, $"ONLY IN '{a.Name.ToUpperInvariant()}'",
            $"'{b.Name}' doesn't have these.", Of(PairKind.OnlyInA), expanded: true));
        Sections.Add(new PairSection(this, PairKind.OnlyInB, $"ONLY IN '{b.Name.ToUpperInvariant()}'",
            $"'{a.Name}' doesn't have these.", Of(PairKind.OnlyInB), expanded: true));
        Sections.Add(new PairSection(this, PairKind.Different, "IN BOTH, BUT DIFFERENT",
            "A different version, on in one and off in the other, or another load order.", Of(PairKind.Different), expanded: true));
        Sections.Add(new PairSection(this, PairKind.Same, "THE SAME IN BOTH",
            "Same version, same on/off and load order.", Of(PairKind.Same), expanded: false));

        int N(PairKind k) => pairs.Count(p => p.Kind == k);
        CompareSummary = N(PairKind.OnlyInA) + N(PairKind.OnlyInB) + N(PairKind.Different) == 0
            ? $"The two profiles are identical: {N(PairKind.Same)} mods, same versions, on/off and load order."
            : $"{N(PairKind.OnlyInA)} only in '{a.Name}' · {N(PairKind.OnlyInB)} only in '{b.Name}' · "
              + $"{N(PairKind.Different)} different · {N(PairKind.Same)} the same";
        CompareEmpty = shown.Count == 0 && q.Length > 0 ? $"No mod matches '{q}'." : "";
    }

    /// <summary>Copies entries across: A's into B (`toB`) or B's into A.
    /// A mod the target lacks is added; one it has takes the source's
    /// version, on/off and load order.</summary>
    public async Task CopyPairs(IReadOnlyCollection<PairItem> items, bool toB)
    {
        if (Selected == null || CompareWith == null) return;
        var (from, to) = toB ? (Selected, CompareWith) : (CompareWith, Selected);
        var ids = items.Where(i => toB ? i.HasA : i.HasB).Select(i => i.ModId).ToList();
        if (ids.Count == 0) return;
        var n = Session.CopyProfileEntries(from.Profile, to.Profile, ids);
        if (n == 0) return;  // the session said why
        Notice = $"Copied {n} mod{(n == 1 ? "" : "s")} from '{from.Name}' into '{to.Name}'.";
        Load();
        await InstallIfLoadout(to.Profile);
    }

    /// <summary>A copy into the loadout only changes its list; the Apply
    /// plan then puts the new mods in the mods folder (or the user closes
    /// it and the Loadout page shows the drift).</summary>
    private async Task InstallIfLoadout(ModProfile target)
    {
        if (Session.IsActiveProfile(target)) await ApplyAsync(target);
    }

    public void RemovePair(PairItem item, bool fromB)
    {
        var card = fromB ? CompareWith : Selected;
        if (card == null || !Session.RemoveProfileEntries(card.Profile, new[] { item.ModId })) return;
        Notice = $"Removed '{item.Name}' from '{card.Name}'.";
        Load();
    }

    private bool CanSwap() => Selected != null && CompareWith != null;

    /// <summary>Swaps A and B.</summary>
    [RelayCommand(CanExecute = nameof(CanSwap))]
    private void Swap()
    {
        var (a, b) = (Selected, CompareWith);
        _reloading = true;
        try
        {
            Selected = b;
            CompareWith = a;
        }
        finally { _reloading = false; }
        (_selectedName, _compareName) = (Selected?.Name, CompareWith?.Name);
        foreach (var c in Cards) c.IsCompared = c == CompareWith;
        FillCompareChoices();
        Fill(force: true);
    }

    private bool CanStopComparing() => CompareWith != null;

    [RelayCommand(CanExecute = nameof(CanStopComparing))]
    private void StopComparing() => CompareWith = null;

    // ── the selected profile's mods ───────────────────────────────────

    /// <summary>Fills the mod list. A reload that left the same mods in
    /// place only re-reads the ticks, so a tick being clicked keeps its
    /// row (and focus).</summary>
    private void FillMods(bool force)
    {
        if (Selected == null || IsComparing)
        {
            Mods.Clear();
            _shownMods.Clear();
            ModsSummary = "";
            OnModSelectionChanged();
            return;
        }
        var profile = Selected.Profile;
        var views = Session.GetProfileMods(profile, Query);
        var mods = views.Select(v => v.Mod).ToList();
        if (!force && mods.Count == _shownMods.Count && mods.Zip(_shownMods).All(p => ReferenceEquals(p.First, p.Second)))
        {
            foreach (var item in Mods) item.Sync();
        }
        else
        {
            // The selection follows the mods (by entry, or by id when
            // the profile was re-read from disk) across a rebuild.
            var selected = new HashSet<string>(Mods.Where(i => i.IsSelected).Select(i => i.Mod.ModId),
                StringComparer.OrdinalIgnoreCase);
            Mods.Clear();
            foreach (var v in views)
                Mods.Add(new ProfileModItem(this, profile, v, !force && selected.Contains(v.Mod.ModId)));
            _shownMods = mods;
        }
        var on = profile.Mods.Count(m => m.IsEnabled);
        ModsSummary = $"{profile.Mods.Count} mods · {on} on"
            + (Selected.IsActive ? " · this is your loadout: change it on the Loadout page"
                : " · edits here change the profile only, not your loadout");
        OnModSelectionChanged();
    }

    public void SetEnabled(ProfileModItem item, bool enabled)
    {
        if (SelectedEditable) Session.SetProfileModEnabled(item.Profile, item.Mod, enabled);
    }

    /// <summary>Saves a row's typed load order. False when it wasn't saved.</summary>
    public bool SetPriority(ProfileModItem item, int priority)
    {
        // A row a reload already replaced has a stale entry: ignore it.
        if (!Mods.Contains(item) || !SelectedEditable) return false;
        var name = item.Name;
        if (!Session.SetProfileModPriority(item.Profile, item.Mod, priority)) return false;
        Notice = $"'{name}' now loads at priority {priority} in '{item.Profile.Name}'.";
        return true;
    }

    public void RemoveFromProfile(ProfileModItem item)
    {
        if (!SelectedEditable || !Session.RemoveModsFromProfile(item.Profile, new[] { item.Mod })) return;
        Notice = $"Removed '{item.Name}' from '{item.Profile.Name}'.";
        Load();
    }

    /// <summary>The list's selected rows, in list order.</summary>
    public List<ProfileModItem> ModSelection => Mods.Where(i => i.IsSelected).ToList();

    /// <summary>The profiles the selected rows can be copied into.</summary>
    public List<ProfileCard> CopyTargets => Cards.Where(c => c != Selected).ToList();

    public void OnModSelectionChanged()
    {
        var n = Mods.Count(i => i.IsSelected);
        ModSelectedCount = n;
        RemoveSelectedLabel = n > 1 ? $"Remove from profile ({n})" : "Remove from profile";
        CopySelectedLabel = n > 1 ? $"Copy {n} to profile…" : "Copy to profile…";
    }

    private bool CanRemoveSelectedMods() => ModSelectedCount > 0 && SelectedEditable;

    /// <summary>Removes every selected row from the selected profile,
    /// after one confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedMods))]
    private void RemoveSelectedMods()
    {
        if (Selected == null) return;
        var profile = Selected.Profile;
        var mods = ModSelection.Select(i => i.Mod).ToList();
        if (!Session.RemoveModsFromProfile(profile, mods)) return;
        Notice = $"Removed {mods.Count} mod{(mods.Count == 1 ? "" : "s")} from '{profile.Name}'.";
        Load();
    }

    /// <summary>The drag payload for the selected rows, or null.</summary>
    public DraggedProfileMods? DragPayload()
    {
        if (Selected == null) return null;
        var mods = ModSelection.Select(i => i.Mod).ToList();
        return mods.Count == 0 ? null : new DraggedProfileMods(Selected.Profile, mods);
    }

    /// <summary>"Copy to profile…": the selected rows into `target`.</summary>
    public void CopySelectedTo(ProfileCard target)
    {
        if (DragPayload() is { } payload) _ = CopyMods(target, payload);
    }

    /// <summary>A drop of list rows on a profile, or "Copy to profile…":
    /// new metadata-only entries in `target`, ids it already lists
    /// skipped. The session tells the user the counts; the page keeps
    /// them too.</summary>
    public async Task CopyMods(ProfileCard target, DraggedProfileMods payload)
    {
        if (ReferenceEquals(target.Profile, payload.Source)) return;
        var name = target.Name;
        var result = Session.CopyModsToProfile(target.Profile, payload.Mods, payload.Source);
        Notice = result.Added > 0
            ? $"Copied {result.Added} mod{(result.Added == 1 ? "" : "s")} from '{payload.Source.Name}' into '{name}'"
              + (result.Skipped > 0 ? $" · {result.Skipped} already there, skipped." : ".")
            : $"Nothing copied into '{name}': "
              + (result.Skipped > 0 ? $"all {result.Skipped} already there." : "no mods picked.");
        var profile = target.Profile;
        Load();
        if (result.Added > 0) await InstallIfLoadout(profile);
    }

    /// <summary>Whether a drag of `payload` can land on `card`.</summary>
    public static bool CanDrop(ProfileCard card, DraggedProfileMods payload)
        => !ReferenceEquals(card.Profile, payload.Source);

    // ── profile actions ───────────────────────────────────────────────

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
        var (wasSelected, wasCompared) = (card == Selected, card == CompareWith);
        if (!Session.RenameProfile(card.Profile)) return;
        if (wasSelected) _selectedName = card.Profile.Name;
        if (wasCompared) _compareName = card.Profile.Name;
        Load();
    }

    public void Describe(ProfileCard card)
    {
        if (Session.SetProfileDescription(card.Profile)) Load();
    }

    public void Delete(ProfileCard card)
    {
        if (!Session.DeleteProfile(card.Profile)) return;
        if (card == Selected) _selectedName = null;
        if (card == CompareWith) _compareName = null;
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
        var ran = await ApplyFlow.RunAsync(Session, profile);
        if (ran) Load();
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

    /// <summary>The DEBUG screenshot harness: select `a` and compare it with `b`.</summary>
    public void Show(string? a, string? b)
    {
        if (FindCard(a) is { } ca) Selected = ca;
        if (FindCard(b) is { } cb && cb != Selected) CompareWith = cb;
    }

    private ProfileCard? FindCard(string? name) =>
        name == null ? null : Cards.FirstOrDefault(c => IsSame(c.Name, name));

    private static bool IsSame(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
