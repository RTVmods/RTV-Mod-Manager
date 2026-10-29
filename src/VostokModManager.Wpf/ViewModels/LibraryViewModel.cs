using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One version of a mod on My Mods (shown when its mod is opened).</summary>
public sealed partial class MyModVersion : ObservableObject
{
    private readonly LibraryViewModel _owner;

    public MyModVersion(LibraryViewModel owner, MyModItem mod, LibraryRow row)
    {
        _owner = owner;
        Mod = mod;
        Row = row;
    }

    public MyModItem Mod { get; }
    public LibraryRow Row { get; }
    public string Version => "v" + Row.Version;
    public string SizeText => Row.SizeText;
    public string FileName => Path.GetFileName(Row.Entry.Path);

    /// <summary>This exact version is the one in the loadout.</summary>
    public bool IsInUse => Row.State is LibraryLiveState.LiveEnabled or LibraryLiveState.LiveDisabled;
    public string StateText => IsInUse ? "In the loadout" : "";
    public string UseLabel => Mod.Row.InLoadout ? "Use this version" : "Add this version";

    [RelayCommand] private Task Use() => _owner.InstallAsync(Row.Entry.Path);
    [RelayCommand] private void Delete() => _owner.DeleteVersion(this);
}

/// <summary>One mod on My Mods: its newest version, whether it is in the
/// loadout, the profiles that use it, and its versions when opened.</summary>
public sealed partial class MyModItem : ObservableObject
{
    private readonly LibraryViewModel _owner;

    public MyModItem(LibraryViewModel owner, LibraryModRow row, bool expanded)
    {
        _owner = owner;
        Row = row;
        Versions = row.Versions.Select(v => new MyModVersion(owner, this, v)).ToList();
        _isExpanded = expanded;
    }

    public LibraryModRow Row { get; }
    public IReadOnlyList<MyModVersion> Versions { get; }
    public string Name => Row.Name;
    public string Newest => "v" + Row.Newest.Version;
    /// <summary>"2 versions · ammo-check": the VostokMods slug when the
    /// source names one (a page UUID says nothing to a reader).</summary>
    public string Sub => Row.SourceText == "—" || Guid.TryParse(Row.SourceText, out _)
        ? Row.VersionsText : $"{Row.VersionsText}  ·  {Row.SourceText}";
    public string StateText => Row.StateText;
    public bool InLoadout => Row.InLoadout;
    public bool CanAdd => Row.State == LibraryLiveState.NotInstalled;
    public string ProfilesText => Row.ProfilesText;
    public string SizeText => Row.SizeText;

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _owner.RememberExpanded(this, value);

    [RelayCommand] private void Toggle() => IsExpanded = !IsExpanded;
    [RelayCommand] private Task Add() => _owner.InstallAsync(Row.Newest.Entry.Path);
    [RelayCommand] private void Remove() => _owner.RemoveFromLoadout(this);
}

/// <summary>My Mods: every mod you have downloaded, one row each, from the
/// library (mods\Library keeps every version the manager has seen; profile
/// switches restore from it). Add a mod to the loadout, take it out, pick
/// another version, or delete versions you no longer need.</summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private LibraryModel? _model;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    public LibraryViewModel(ManagerSession session) : base(session) { }

    public ObservableCollection<MyModItem> Mods { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _cleanupNote = "";
    [ObservableProperty] private LibrarySortColumn _sortColumn = LibrarySortColumn.Mod;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty;

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
        Mods.Clear();
        foreach (var r in _model.ModRows) Mods.Add(new MyModItem(this, r, _expanded.Contains(r.ModId)));
        IsEmpty = Mods.Count == 0;
        Header = _model.ModsHeaderText;
        SortColumn = _model.SortColumn;
        var plan = _model.PlanCleanup();
        CleanupNote = plan.IsEmpty ? "No old versions to clean up."
            : $"{plan.Entries.Count} old version(s) no profile uses · {LibraryModel.FormatSize(plan.Bytes)}";
    }

    internal void RememberExpanded(MyModItem item, bool expanded)
    {
        if (expanded) _expanded.Add(item.Row.ModId);
        else _expanded.Remove(item.Row.ModId);
    }

    /// <summary>Installs a library file into the loadout (a newer or older
    /// version replaces the one there), then offers what it needs.</summary>
    internal async Task InstallAsync(string path)
    {
        if (IsBusy || !File.Exists(path)) return;
        IsBusy = true;
        try
        {
            var result = await Session.InstallModFilesAsync(new[] { path });
            if (result.InstalledModIds.Count > 0)
                await Session.CheckAndPromptMissingDepsAsync(result.InstalledModIds,
                    Views.MissingDependenciesWindow.Resolver(() => Application.Current?.MainWindow));
        }
        finally
        {
            IsBusy = false;
            Load();
        }
    }

    /// <summary>Takes the mod out of the loadout (asks first). The library
    /// keeps its versions unless the user says otherwise.</summary>
    internal void RemoveFromLoadout(MyModItem item)
    {
        if (item.Row.Live is { } live && Session.DeleteMod(live)) Load();
    }

    internal void DeleteVersion(MyModVersion version)
    {
        if (_model == null) return;
        if (_model.Delete(new[] { version.Row.Entry }) > 0) Fill();
    }

    [RelayCommand]
    private void Sort(string column)
    {
        if (_model == null || !Enum.TryParse<LibrarySortColumn>(column, out var c)) return;
        _model.SortBy(c);
        Fill();
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
