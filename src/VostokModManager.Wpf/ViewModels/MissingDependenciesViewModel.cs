using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One missing dependency row; the URL box writes straight
/// into the resolver's row.</summary>
public sealed partial class MissingDependencyItem : ObservableObject
{
    public MissingDependencyItem(MissingDependency row) => Row = row;

    public MissingDependency Row { get; }
    public string ModId => Row.ModId;
    public string Kind => Row.Kind;
    public string KindBrushKey => Row.Required ? "B.AmberText" : "B.Blue";
    public string NeededBy => "needed by " + Row.ParentName;

    public string SourceInput
    {
        get => Row.SourceInput;
        set
        {
            if (Row.SourceInput == value) return;
            Row.SourceInput = value ?? "";
            OnPropertyChanged();
        }
    }

    public string StatusText => Row.StatusText;
    public string StatusBrushKey => Row.Status switch
    {
        MissingDependencyStatus.Ready => "B.Blue",
        MissingDependencyStatus.Downloading => "B.Blue",
        MissingDependencyStatus.Invalid => "B.AmberText",
        MissingDependencyStatus.Installed => "B.Green",
        MissingDependencyStatus.Failed => "B.SalmonText",
        _ => "B.Text3",
    };
    public bool IsInstalled => Row.Status == MissingDependencyStatus.Installed;
    public bool IsDownloading => Row.Status == MissingDependencyStatus.Downloading;

    /// <summary>Re-reads the row after the resolver changed it.</summary>
    public void Sync()
    {
        OnPropertyChanged(nameof(SourceInput));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsDownloading));
    }
}

/// <summary>The Missing Dependencies window over a
/// <see cref="MissingDependencyResolver"/>: paste a VostokMods URL or
/// slug per row, Find opens a site search, Download fetches and
/// installs, with a log underneath.</summary>
public sealed partial class MissingDependenciesViewModel : ObservableObject, IDisposable
{
    private readonly MissingDependencyResolver _resolver;

    public MissingDependenciesViewModel(MissingDependencyResolver resolver)
    {
        _resolver = resolver;
        foreach (var r in resolver.Rows) Rows.Add(new MissingDependencyItem(r));
        SyncLog();
        _resolver.Changed += OnChanged;
    }

    public string Title => Rows.Count == 1 ? "1 missing dependency" : $"{Rows.Count} missing dependencies";
    public string Intro => _resolver.Intro
        + (_resolver.LibraryResolved > 0
            ? $" ({_resolver.LibraryResolved} other dependenc{(_resolver.LibraryResolved == 1 ? "y was" : "ies were")} copied in from your library.)"
            : "");

    public ObservableCollection<MissingDependencyItem> Rows { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private bool _isBusy;
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        DownloadCommand.NotifyCanExecuteChanged();
    }
    public bool IsIdle => !IsBusy;
    public bool HasLog => Log.Count > 0;

    private void OnChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.BeginInvoke(new Action(OnChanged)); return; }
        foreach (var r in Rows) r.Sync();
        SyncLog();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private void SyncLog()
    {
        for (var i = Log.Count; i < _resolver.Log.Count; i++) Log.Add(_resolver.Log[i]);
        OnPropertyChanged(nameof(HasLog));
    }

    [RelayCommand]
    private void Find(MissingDependencyItem? row)
    {
        if (row != null) PromptOwner.OpenUrl(MissingDependencyResolver.SearchUrl(row.ModId));
    }

    private bool CanDownload(MissingDependencyItem? row) => !IsBusy && row is { IsInstalled: false };

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download(MissingDependencyItem? row)
    {
        if (row == null || IsBusy || row.IsInstalled) return;
        IsBusy = true;
        try { await _resolver.DownloadAsync(row.Row); }
        finally { IsBusy = false; OnChanged(); }
    }

    [RelayCommand]
    private async Task DownloadAll()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var n = await _resolver.DownloadAllAsync();
            if (n == 0 && !Rows.Any(r => MissingDependencyResolver.TryParseSourceInput(r.SourceInput, out _) && !r.IsInstalled))
                _resolver.Log.Add("Nothing to download: paste a VostokMods URL or slug into a row first.");
        }
        finally { IsBusy = false; OnChanged(); }
    }

    public void Dispose() => _resolver.Changed -= OnChanged;
}
