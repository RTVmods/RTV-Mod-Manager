using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _watchDebounce;

    public ShellViewModel(ManagerSession session)
    {
        Session = session;
        Mods = new ModsViewModel(session);
        Nav = new ObservableCollection<NavItem>
        {
            new("mods", "Mods", "Icon.Mods", Mods),
            new("profiles", "Profiles", "Icon.Profiles", new PlaceholderViewModel(session, "Profiles")),
            new("saves", "Saves", "Icon.Saves", new SavesViewModel(session)),
            new("browse", "Browse VostokMods", "Icon.Browse", new PlaceholderViewModel(session, "Browse VostokMods")),
            new("library", "Library", "Icon.Library", new LibraryViewModel(session)),
            new("tools", "Tools", "Icon.Tools", new ToolsViewModel(session)),
            new("settings", "Settings", "Icon.Settings", new PlaceholderViewModel(session, "Settings")),
        };
        _selected = Nav[0];
        _selected.IsSelected = true;
        ManagerVersion = "Manager " + ManagerSession.ManagerInstalledVersion();

        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (Session.IsUpdating) return;  // the update's own refresh follows
            Session.Refresh();
            StatusText = "Mods folder changed — list refreshed.";
        };

        session.Changed += OnSessionChanged;
        session.Status += OnStatus;
        Imports = new Wpf.Services.ImportFlow(session, () => Application.Current?.MainWindow);
        Mods.ImportRequested += async files =>
        {
            foreach (var f in files) await Imports.RunFileAsync(f);
        };
    }

    public ManagerSession Session { get; }
    public ModsViewModel Mods { get; }
    public ObservableCollection<NavItem> Nav { get; }

    /// <summary>Set by the window: shows the setup window (first run,
    /// adopting profiles) and returns once it closes.</summary>
    public Func<SetupReason, Task>? ShowSetupAsync { get; set; }

    /// <summary>Bringing mod lists and modpacks into a profile.</summary>
    public Wpf.Services.ImportFlow Imports { get; }

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

    // ── launch panel and footer ───────────────────────────────────────

    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _nextLaunchText = "";
    public ObservableCollection<LaunchCheck> Checks { get; } = new();

    [ObservableProperty] private string _managerVersion;
    [ObservableProperty] private string _managerUpdate = "";
    [ObservableProperty] private string _loaderVersion = "";
    [ObservableProperty] private string _loaderUpdate = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    private void OnStatus(StatusArea area, string text)
    {
        // Scans and checks narrate a lot; the title strip shows the latest.
        StatusText = text;
    }

    [RelayCommand]
    private void Launch()
    {
        if (!Session.LaunchGame()) return;
        // The watch reports a crash through the session's own prompt.
    }

    [RelayCommand]
    private void Undo() => Session.RestoreCheckpoint();

    [RelayCommand]
    private async Task ManagerUpdateClick() => Act(await Session.HandleManagerClickAsync());

    [RelayCommand]
    private async Task LoaderUpdateClick() => Act(await Session.HandleMmlClickAsync());

    private static void Act(SelfUpdateAction action)
    {
        if (action.OpenUrl != null)
        {
            try { Process.Start(new ProcessStartInfo(action.OpenUrl) { UseShellExecute = true }); } catch { }
        }
        // The swap script waits for this process to go before copying the new .exe in.
        if (action.MustExit) Application.Current.Shutdown();
    }

    // ── startup ───────────────────────────────────────────────────────

    /// <summary>The app's startup work, after the window is up.</summary>
    public async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Session.StartupAsync();
            if (!result.ModsFolderReadable)
            {
                if (ShowSetupAsync != null) await ShowSetupAsync(SetupReason.ModsFolderMissing);
                return;
            }
            WatchModsFolder();
            if (result.Has(StartupStep.OfferProfileMigration) || result.Has(StartupStep.OfferFirstRunBackup))
            {
                if (ShowSetupAsync != null)
                    await ShowSetupAsync(Session.IsFirstRun ? SetupReason.FirstRun : SetupReason.AdoptProfiles);
                Session.Refresh();
            }
            if (result.Has(StartupStep.CheckUpdates)) await Session.CheckUpdatesAsync();
            if (result.Has(StartupStep.CheckMmlVersion))
            {
                var mml = await Session.CheckMmlVersionAsync();
                LoaderUpdate = mml.IsOutdated ? $"Update to {mml.Latest}" : "";
            }
            if (result.Has(StartupStep.CheckManagerUpdate))
            {
                var mgr = await Session.CheckManagerUpdateAsync(forceFresh: true);
                ManagerUpdate = mgr.IsOutdated ? $"Update to {mgr.Latest}" : "";
            }
            if (Session.IsFirstRun) Session.FinishFirstRun();
        }
        catch (Exception ex)
        {
            Session.Prompt.Error("Startup didn't finish", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A .vmz landing in or leaving the mods folder (Explorer,
    /// the in-game loader) refreshes the list, debounced so a multi-file
    /// copy is one refresh.</summary>
    public void WatchModsFolder()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(Session.ModsDir)) return;
        try
        {
            _watcher = new FileSystemWatcher(Session.ModsDir)
            {
                Filter = "*.vmz",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler kick = (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _watchDebounce.Stop();
                _watchDebounce.Start();
            });
            _watcher.Created += kick;
            _watcher.Deleted += kick;
            _watcher.Changed += kick;
            _watcher.Renamed += (s, e) => kick(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null;  // unsupported file system: manual refresh only
        }
    }

    public void Shutdown()
    {
        Session.StopWatchingGame();
        _watcher?.Dispose();
    }

    private void OnSessionChanged()
    {
        // Events come on the UI thread (the session is UI-thread-affine).
        ProfileName = Session.ActiveProfile?.Name ?? "No profile yet";
        var ver = MmlInstall.DetectInstalledVersion(Session.ModsDir);
        LoaderVersion = string.IsNullOrEmpty(ver) ? "Mod Loader not found" : $"Mod Loader {ver}";

        Nav[0].Count = Session.CountMods().Total.ToString();
        Nav[1].Count = Session.AllProfiles.Count == 0 ? "" : Session.AllProfiles.Count.ToString();

        try
        {
            var saves = Session.GetSavesView();
            Nav[2].Count = saves.Slots.Count(s => s.Occupied) is var n and > 0 ? n.ToString() : "";
            var next = saves.Slots.FirstOrDefault(s => s.IsActiveSlot && s.Occupied);
            NextLaunchText = next == null ? "" : $"Slot {next.Number} · {next.Title}";
        }
        catch
        {
            NextLaunchText = "";
        }

        Checks.Clear();
        var r = Session.GetLaunchReadiness();
        if (!r.ModsFolderFound) Checks.Add(new("Mods folder not found", LaunchCheckLevel.Problem));
        else if (!r.LoadOrderValid) Checks.Add(new(r.DuplicateModIds.Count > 0
            ? $"{r.DuplicateModIds.Count} mod id(s) enabled twice" : "mod_config.cfg unreadable", LaunchCheckLevel.Problem));
        else Checks.Add(new($"{r.EnabledCount} mods on, load order valid", LaunchCheckLevel.Ok));
        if (r.MissingDependencyCount > 0)
            Checks.Add(new($"{r.MissingDependencyCount} missing dependenc{(r.MissingDependencyCount == 1 ? "y" : "ies")}", LaunchCheckLevel.Problem));
        if (r.OutdatedCount > 0)
            Checks.Add(new(r.OutdatedCount == 1 ? "1 update waiting" : $"{r.OutdatedCount} updates waiting", LaunchCheckLevel.Notice));
        if (r.ConflictCount > 0)
            Checks.Add(new(r.ConflictCount == 1 ? "1 known conflict" : $"{r.ConflictCount} known conflicts", LaunchCheckLevel.Notice));
        if (r.SaveSlotsWarning != null)
            Checks.Add(new("Save slots need attention", LaunchCheckLevel.Notice));

        foreach (var item in Nav) item.Page.Reload();
    }
}

/// <summary>Why the setup window opens.</summary>
public enum SetupReason { FirstRun, AdoptProfiles, ModsFolderMissing, FromSettings }
