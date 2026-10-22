using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A severity tier as its banner shows it. Rows of one refresh
/// share the instance, which is what the list groups on.</summary>
public sealed class ConflictTier
{
    public ConflictTier(ConflictSeverity severity, int count)
    {
        Severity = severity;
        Label = ManagerSession.TierLabel(severity, count);
        Tooltip = ManagerSession.TierTooltip(severity);
        (FillKey, EdgeKey, TextKey) = severity switch
        {
            ConflictSeverity.Blocking => ("B.SalmonFill", "B.SalmonEdge", "B.SalmonText"),
            ConflictSeverity.Behavior => ("B.AmberFill", "B.AmberEdge", "B.AmberText"),
            _ => ("B.BlueFill", "B.BlueEdge", "B.Blue"),
        };
    }

    public ConflictSeverity Severity { get; }
    public string Label { get; }
    public string Tooltip { get; }
    public string FillKey { get; }
    public string EdgeKey { get; }
    public string TextKey { get; }
}

/// <summary>One conflict row.</summary>
public sealed class ConflictRowItem
{
    public ConflictRowItem(ConflictView view, ConflictTier tier, bool isFocused)
    {
        View = view;
        Tier = tier;
        IsFocused = isFocused;
    }

    public ConflictView View { get; }
    public ConflictDetector.Conflict Conflict => View.Conflict;
    public ConflictTier Tier { get; }
    /// <summary>Involves the mod the window was opened for.</summary>
    public bool IsFocused { get; }

    public string Title => View.Title;
    public string Subtitle => View.Subtitle;
    public string RawTooltip => View.RawTooltip;
    /// <summary>Earliest-loading first: the last one loads last.</summary>
    public IReadOnlyList<string> ModNames => View.ModNames;
    public string WinnerText => View.Winner.Text;
    public string WinnerWhy => View.Winner.Why;
    public string WinnerBrushKey => View.Winner.Tone switch
    {
        WinnerTone.HardFail => "B.SalmonText",
        WinnerTone.Muted => "B.Text3",
        WinnerTone.Winner => "B.Green",
        _ => "B.Text",
    };
    public bool HasResolveButton => View.HasResolveButton;
    public string ResolveHint => View.ResolveHint;

    /// <summary>Identity across refreshes (to keep the selection).</summary>
    public string Identity => View.Type + "\n" + View.Key + "\n" + string.Join(",", View.ModIds);
}

/// <summary>The Conflicts window: every detected conflict by severity
/// tier, which mod wins, and the way into the detail / AI resolve.</summary>
public sealed partial class ConflictsViewModel : ObservableObject, IDisposable
{
    private readonly ManagerSession _session;
    private readonly string? _focusModId;
    private bool _refreshQueued;

    public ConflictsViewModel(ManagerSession session, string? focusModId = null)
    {
        _session = session;
        _focusModId = string.IsNullOrWhiteSpace(focusModId) ? null : focusModId;
        if (_focusModId != null)
        {
            var e = session.Registry.FindById(_focusModId);
            FocusName = e != null && !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : _focusModId;
        }
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConflictRowItem.Tier)));
        _session.Changed += OnSessionChanged;
        Refresh();
    }

    public ObservableCollection<ConflictRowItem> Rows { get; } = new();
    public ICollectionView RowsView { get; }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private ConflictRowItem? _selected;
    partial void OnSelectedChanged(ConflictRowItem? value) => OnPropertyChanged(nameof(HasSelection));
    public bool HasSelection => Selected != null;
    [ObservableProperty] private bool _canResolve;

    /// <summary>Show only the conflicts the focus mod takes part in.</summary>
    [ObservableProperty] private bool _onlyFocused;
    partial void OnOnlyFocusedChanged(bool value) => Refresh();

    public bool HasFocus => _focusModId != null;
    public string FocusName { get; } = "";
    public string FocusToggleText => $"Only conflicts involving {FocusName}";
    public string FocusNote => $"Conflicts involving {FocusName} are marked and listed first in their tier.";

    /// <summary>Opens a row's detail (set by the window).</summary>
    public Action<ConflictRowItem>? OpenDetails { get; set; }
    /// <summary>Runs the AI resolve for a row (set by the window).</summary>
    public Func<ConflictRowItem, Task>? RunResolve { get; set; }

    public void Refresh()
    {
        _refreshQueued = false;
        var keep = Selected?.Identity;

        HashSet<ConflictDetector.Conflict>? focused = null;
        if (_focusModId != null)
        {
            var entry = _session.Registry.FindById(_focusModId);
            var involving = entry != null ? _session.ConflictsInvolving(entry) : _session.ConflictsInvolving(_focusModId);
            focused = new HashSet<ConflictDetector.Conflict>(involving.Select(v => v.Conflict), ReferenceEqualityComparer.Instance);
        }

        var views = _session.GetConflictViews()
            .Select(v => (View: v, Focused: focused != null && focused.Contains(v.Conflict)))
            .Where(t => !OnlyFocused || t.Focused)
            .ToList();
        var tiers = views.GroupBy(t => t.View.Severity)
            .ToDictionary(g => g.Key, g => new ConflictTier(g.Key, g.Count()));

        Rows.Clear();
        // Tier order first (the session's list order), then the focus
        // mod's conflicts ahead of the rest; OrderBy is stable.
        foreach (var (view, isFocused) in views
                     .OrderBy(t => (int)t.View.Severity)
                     .ThenBy(t => t.Focused ? 0 : 1))
            Rows.Add(new ConflictRowItem(view, tiers[view.Severity], isFocused));

        StatusText = _session.ConflictsStatusText();
        IsEmpty = Rows.Count == 0;
        EmptyText = OnlyFocused && _session.LastConflicts.Count > 0
            ? $"{FocusName} isn't part of any detected conflict."
            : "No conflicts detected. Every enabled mod loads without stepping on another.";
        Selected = Rows.FirstOrDefault(r => r.Identity == keep);
#if AI_RESOLVER
        CanResolve = !_session.IsResolving;
#else
        CanResolve = false;
#endif
    }

    /// <summary>The first row involving the focus mod (to scroll to).</summary>
    public ConflictRowItem? FirstFocused => Rows.FirstOrDefault(r => r.IsFocused);

    private void OnSessionChanged()
    {
        // Several Changed in a row (rescan, redetect) → one refresh.
        if (_refreshQueued) return;
        _refreshQueued = true;
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(new Action(Refresh));
    }

    [RelayCommand]
    private void Rescan() => _session.Refresh();

    [RelayCommand]
    private void Details(ConflictRowItem? row)
    {
        row ??= Selected;
        if (row != null) OpenDetails?.Invoke(row);
    }

    [RelayCommand]
    private async Task Resolve(ConflictRowItem? row)
    {
        if (row == null || RunResolve == null || !row.HasResolveButton) return;
        await RunResolve(row);
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}
