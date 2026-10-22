using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A page the side menu can open.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(ManagerSession session) => Session = session;

    public ManagerSession Session { get; }

    /// <summary>Re-reads the session after it changed.</summary>
    public virtual void Reload() { }

    /// <summary>Called when the page is opened from the menu.</summary>
    public virtual void OnShown() { }
}

/// <summary>A page that isn't built yet.</summary>
public sealed class PlaceholderViewModel : PageViewModel
{
    public PlaceholderViewModel(ManagerSession session, string title) : base(session) => Title = title;
    public string Title { get; }
}

/// <summary>One entry in the side menu.</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string title, string icon, PageViewModel page)
    {
        Key = key;
        Title = title;
        IconKey = icon;
        Page = page;
    }

    public string Key { get; }
    public string Title { get; }
    public string IconKey { get; }
    public PageViewModel Page { get; }

    /// <summary>The small number at the right of the entry; empty for none.</summary>
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A pre-flight line in the launch panel.</summary>
public sealed record LaunchCheck(string Text, LaunchCheckLevel Level);
public enum LaunchCheckLevel { Ok, Notice, Problem }

/// <summary>The window: side menu, launch panel and whichever page is open.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ManagerSession session)
    {
        Session = session;
        Mods = new ModsViewModel(session);
        Nav = new ObservableCollection<NavItem>
        {
            new("mods", "Mods", "Icon.Mods", Mods),
            new("profiles", "Profiles", "Icon.Profiles", new PlaceholderViewModel(session, "Profiles")),
            new("saves", "Saves", "Icon.Saves", new PlaceholderViewModel(session, "Saves")),
            new("browse", "Browse VostokMods", "Icon.Browse", new PlaceholderViewModel(session, "Browse VostokMods")),
            new("library", "Library", "Icon.Library", new PlaceholderViewModel(session, "Library")),
            new("tools", "Tools", "Icon.Tools", new PlaceholderViewModel(session, "Tools")),
            new("settings", "Settings", "Icon.Settings", new PlaceholderViewModel(session, "Settings")),
        };
        _selected = Nav[0];
        _selected.IsSelected = true;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        ManagerVersion = v == null ? "Manager" : $"Manager {v.Major}.{v.Minor}.{v.Build}";

        session.Changed += OnSessionChanged;
        session.Status += (area, text) => StatusText = text;
    }

    public ManagerSession Session { get; }
    public ModsViewModel Mods { get; }
    public ObservableCollection<NavItem> Nav { get; }

    [ObservableProperty] private NavItem _selected;

    partial void OnSelectedChanged(NavItem? oldValue, NavItem newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        newValue.Page.OnShown();
    }

    [RelayCommand]
    private void Navigate(NavItem item) => Selected = item;

    /// <summary>Opens a page by its key ("mods", "profiles", ...).</summary>
    public void Open(string key)
    {
        var item = Nav.FirstOrDefault(n => n.Key == key);
        if (item != null) Selected = item;
    }

    [RelayCommand]
    private void Go(string key) => Open(key);

    [RelayCommand]
    private void Undo()
    {
        // Wired to the checkpoint restore once the launch area lands.
        Session.Prompt.Inform("Undo", "Restoring the last checkpoint is not wired up yet.");
    }

    // ── launch panel ──────────────────────────────────────────────────

    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _nextLaunchText = "";
    public ObservableCollection<LaunchCheck> Checks { get; } = new();

    [ObservableProperty] private string _managerVersion;
    [ObservableProperty] private string _loaderVersion = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private void Launch()
    {
        // Wired to the session's launch flow once the launch area lands.
        Session.Prompt.Inform("Launch", "Launching from the new manager is not wired up yet.");
    }

    /// <summary>The app's startup work, after the window is up.</summary>
    public async Task StartAsync()
    {
        IsBusy = true;
        StatusText = $"Scanning {Session.ModsDir} …";
        try
        {
            await Task.Yield();
            if (!Session.Rescan())
            {
                StatusText = $"Can't read {Session.ModsDir}. Is the game installed at the default Steam path?";
            }
            Session.ReloadProfiles();
            Session.Refresh();
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnSessionChanged()
    {
        // Events come on the UI thread (the session is UI-thread-affine).
        ProfileName = Session.ActiveProfile?.Name ?? "No profile yet";
        var ver = MmlInstall.DetectInstalledVersion(Session.ModsDir);
        LoaderVersion = string.IsNullOrEmpty(ver) ? "Mod Loader not found" : $"Mod Loader {ver}";

        Nav[0].Count = Session.Registry.Entries.Count.ToString();
        Nav[1].Count = Session.AllProfiles.Count == 0 ? "" : Session.AllProfiles.Count.ToString();

        Checks.Clear();
        var outdated = Session.Registry.Entries.Count(Session.IsOutdated);
        var conflicts = Session.LastConflicts.Count;
        Checks.Add(new LaunchCheck(
            Session.Registry.Entries.Count == 0 ? "No mods installed" : $"{Session.Registry.Enabled().Count} mods on",
            LaunchCheckLevel.Ok));
        if (outdated > 0)
            Checks.Add(new LaunchCheck(outdated == 1 ? "1 update waiting" : $"{outdated} updates waiting", LaunchCheckLevel.Notice));
        if (conflicts > 0)
            Checks.Add(new LaunchCheck(conflicts == 1 ? "1 known conflict" : $"{conflicts} known conflicts", LaunchCheckLevel.Notice));

        foreach (var n in Nav) n.Page.Reload();
    }
}
