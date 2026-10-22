using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>What a row's status column says, which also picks its colour.</summary>
public enum RowStatusKind { UpToDate, Update, Conflict, Testing, Locked, Disabled, Unlinked, BuiltIn, Ambiguous, Newer, Unknown }

/// <summary>One mod in the Mods list.</summary>
public sealed partial class ModRow : ObservableObject
{
    public ModRow(ModEntry entry) => Entry = entry;

    public ModEntry Entry { get; }
    public string Name => string.IsNullOrEmpty(Entry.DisplayName) ? System.IO.Path.GetFileName(Entry.Path) : Entry.DisplayName;
    public string Version => Entry.Version;
    public int Priority => Entry.Priority;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private RowStatusKind _statusKind;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isTesting;
}

/// <summary>A filter tab above the list, with its count.</summary>
public sealed partial class FilterTab : ObservableObject
{
    public FilterTab(string key, string label) { Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;
}

public sealed partial class ModsViewModel : PageViewModel
{
    public ModsViewModel(ManagerSession session) : base(session)
    {
        Tabs = new ObservableCollection<FilterTab>
        {
            new("all", "All"), new("enabled", "Enabled"), new("disabled", "Disabled"),
            new("updates", "Updates"), new("conflicts", "Conflicts"), new("testing", "Testing"),
        };
        Tabs[0].IsActive = true;
    }

    public ObservableCollection<FilterTab> Tabs { get; }
    public ObservableCollection<ModRow> Rows { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private ModRow? _selectedRow;
    [ObservableProperty] private string _profileName = "";

    // Status chips
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _unlinkedCount;
    [ObservableProperty] private string _footer = "";

    partial void OnQueryChanged(string value) => Rebuild();
    partial void OnFilterChanged(string value)
    {
        foreach (var t in Tabs) t.IsActive = t.Key == value;
        Rebuild();
    }

    [RelayCommand]
    private void SetFilter(string key) => Filter = key;

    public override void Reload() => Rebuild();

    /// <summary>The mods this page shows: the active profile's plus
    /// locked ones (all of them before the profile model is adopted).</summary>
    private IEnumerable<ModEntry> InScope()
    {
        var profile = Session.ActiveProfile;
        if (profile == null) return Session.Registry.Entries;
        var ids = new HashSet<string>(profile.Mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
        foreach (var id in Session.ProtectedModIds()) ids.Add(id);
        return Session.Registry.Entries.Where(e => string.IsNullOrEmpty(e.ModId) || ids.Contains(e.ModId));
    }

    private void Rebuild()
    {
        ProfileName = Session.ActiveProfile?.Name ?? "All installed mods";
        var conflicted = new HashSet<string>(
            Session.LastConflicts.SelectMany(c => c.ModIds), StringComparer.OrdinalIgnoreCase);

        var all = InScope()
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => MakeRow(e, conflicted))
            .ToList();

        Count("all", all.Count);
        Count("enabled", all.Count(r => r.IsEnabled));
        Count("disabled", all.Count(r => !r.IsEnabled));
        Count("updates", all.Count(r => r.StatusKind == RowStatusKind.Update));
        Count("conflicts", all.Count(r => conflicted.Contains(r.Entry.ModId)));
        Count("testing", all.Count(r => r.IsTesting));
        UpdateCount = all.Count(r => r.StatusKind == RowStatusKind.Update);
        ConflictCount = Session.LastConflicts.Count;
        UnlinkedCount = Session.UnlinkedMods().Count;
        Footer = $"{all.Count(r => r.IsEnabled)} of {all.Count} enabled";

        var q = Query.Trim();
        var keep = SelectedRow?.Entry.ModId;
        Rows.Clear();
        foreach (var r in all)
        {
            if (q.Length > 0
                && r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && r.Entry.ModId.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && System.IO.Path.GetFileName(r.Entry.Path).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var pass = Filter switch
            {
                "enabled" => r.IsEnabled,
                "disabled" => !r.IsEnabled,
                "updates" => r.StatusKind == RowStatusKind.Update,
                "conflicts" => conflicted.Contains(r.Entry.ModId),
                "testing" => r.IsTesting,
                _ => true,
            };
            if (pass) Rows.Add(r);
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Entry.ModId == keep) ?? Rows.FirstOrDefault();
    }

    private void Count(string key, int n)
    {
        var t = Tabs.FirstOrDefault(x => x.Key == key);
        if (t != null) t.Count = n;
    }

    private ModRow MakeRow(ModEntry e, HashSet<string> conflicted)
    {
        var row = new ModRow(e)
        {
            IsEnabled = e.IsEnabled,
            IsLocked = Session.IsLocked(e),
            IsTesting = Session.IsTesting(e),
        };
        (row.StatusKind, row.Status) = StatusOf(e, conflicted);
        return row;
    }

    private (RowStatusKind, string) StatusOf(ModEntry e, HashSet<string> conflicted)
    {
        if (Session.IsBuiltInActive(e.ModId)) return (RowStatusKind.BuiltIn, "Built in");
        if (Session.TryGetLatest(e, out var latest))
        {
            if (Session.IsOutdated(e)) return (RowStatusKind.Update, $"Update → {latest}");
        }
        if (!string.IsNullOrEmpty(e.ModId) && conflicted.Contains(e.ModId)) return (RowStatusKind.Conflict, "Conflict");
        if (Session.IsTesting(e)) return (RowStatusKind.Testing, "Testing build");
        if (Session.IsLocked(e)) return (RowStatusKind.Locked, "Locked");
        if (!e.IsEnabled) return (RowStatusKind.Disabled, "Disabled");
        if (!e.Source.IsValid) return (RowStatusKind.Unlinked, "Not linked");
        if (string.IsNullOrEmpty(latest)) return (RowStatusKind.Unknown, "Not checked yet");
        if (ManagerSession.VersionsAreAmbiguous(e.Version, latest)) return (RowStatusKind.Ambiguous, "Same release");
        if (ModRegistry.CompareVersions(e.Version, latest) > 0) return (RowStatusKind.Newer, "Newer than site");
        return (RowStatusKind.UpToDate, "Up to date");
    }

    // ── the selected mod (details panel) ──────────────────────────────

    public string SelectedName => SelectedRow?.Name ?? "";
    public string SelectedMeta => SelectedRow == null ? "" :
        $"v{SelectedRow.Version} · load order {SelectedRow.Priority}" +
        (SelectedRow.Entry.Source.IsValid ? $" · {SelectedRow.Entry.Source.Id}" : " · not linked to VostokMods");
    public string SelectedDescription => SelectedRow == null ? "" :
        string.IsNullOrWhiteSpace(SelectedRow.Entry.Description)
            ? "This mod's mod.txt has no description. Linked mods show the description from their VostokMods page."
            : SelectedRow.Entry.Description;
    public IReadOnlyList<string> SelectedRequires => SelectedRow?.Entry.RequiredDependencies ?? new List<string>();
    public IReadOnlyList<ConflictDetector.Conflict> SelectedConflicts =>
        SelectedRow == null || string.IsNullOrEmpty(SelectedRow.Entry.ModId)
            ? Array.Empty<ConflictDetector.Conflict>()
            : Session.LastConflicts.Where(c => c.ModIds.Contains(SelectedRow.Entry.ModId, StringComparer.OrdinalIgnoreCase)).ToList();

    partial void OnSelectedRowChanged(ModRow? value)
    {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedMeta));
        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(SelectedRequires));
        OnPropertyChanged(nameof(SelectedConflicts));
    }
}
