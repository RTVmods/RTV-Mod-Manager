using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>Every archived version the manager keeps in mods\Library: what
/// profile switches restore from.</summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private LibraryModel? _model;

    public LibraryViewModel(ManagerSession session) : base(session) { }

    public ObservableCollection<LibraryRow> Rows { get; } = new();
    public List<LibraryRow> SelectedRows { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _cleanupNote = "";
    [ObservableProperty] private LibrarySortColumn _sortColumn = LibrarySortColumn.Mod;

    partial void OnQueryChanged(string value)
    {
        if (_model == null) return;
        _model.Filter = value;
        Fill();
    }

    public override void OnShown() => Load();

    public override void Reload()
    {
        // Only re-read the folder while the page is the one open.
        if (_model != null) Load();
    }

    private void Load()
    {
        _model ??= Session.OpenLibrary();
        _model.Reload();
        _model.Filter = Query;
        Fill();
    }

    private void Fill()
    {
        if (_model == null) return;
        Rows.Clear();
        foreach (var r in _model.Rows) Rows.Add(r);
        Header = _model.HeaderText;
        SortColumn = _model.SortColumn;
        var plan = _model.PlanCleanup();
        CleanupNote = plan.IsEmpty ? "No old versions to clean up."
            : $"{plan.Entries.Count} old version(s) no profile uses · {LibraryModel.FormatSize(plan.Bytes)}";
    }

    [RelayCommand]
    private void Sort(string column)
    {
        if (_model == null || !Enum.TryParse<LibrarySortColumn>(column, out var c)) return;
        _model.SortBy(c);
        Fill();
    }

    [RelayCommand]
    private async Task InstallSelected()
    {
        if (_model == null || SelectedRows.Count == 0) return;
        var paths = _model.PathsToInstall(SelectedRows.Select(r => r.Entry));
        if (paths.Count == 0) return;
        await Session.InstallModFilesAsync(paths, null);
        Load();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_model == null || SelectedRows.Count == 0) return;
        if (_model.Delete(SelectedRows.Select(r => r.Entry).ToList()) > 0) Fill();
    }

    [RelayCommand]
    private void CleanUp()
    {
        if (_model == null) return;
        _model.CleanUpOldVersions();
        Fill();
    }

    [RelayCommand]
    private void Refresh() => Load();

    [RelayCommand]
    private void OpenFolder()
    {
        if (_model == null) return;
        try { Process.Start("explorer.exe", $"\"{_model.LibraryDir}\""); } catch { /* not there yet */ }
    }
}
