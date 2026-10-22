using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>The Settings page: folders, launch, save slots, the AI
/// edition's Claude paths, update checks, and About.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ShellViewModel _shell;
    private SettingsValues _saved;

    public SettingsViewModel(ManagerSession session, ShellViewModel shell) : base(session)
    {
        _shell = shell;
        _saved = SettingsValues.From(session.Settings);
        Load();
    }

#if AI_RESOLVER
    public bool IsAiEdition => true;
    public string Edition => "AI edition";
#else
    public bool IsAiEdition => false;
    public string Edition => "Integrated edition";
#endif

    public string VersionLine => $"Vostok Mod Manager {ManagerSession.ManagerInstalledVersion()} · {Edition}";
    public string SettingsFolder => AppPaths.ManagerDir;

    [ObservableProperty] private string _modsDir = "";
    [ObservableProperty] private string _claudePath = "";
    [ObservableProperty] private string _gameSourcePath = "";
    [ObservableProperty] private bool _skipLoaderUi;
    [ObservableProperty] private bool _saveSlotsBuiltIn;
    [ObservableProperty] private string _claudeStatus = "";
    [ObservableProperty] private string _managerStatus = "";
    [ObservableProperty] private string _loaderStatus = "";
    [ObservableProperty] private bool _dirty;

    public override void OnShown() => Load();

    private bool _loading;

    private void Load()
    {
        _loading = true;
        _saved = SettingsValues.From(Session.Settings);
        ModsDir = string.IsNullOrEmpty(_saved.ModsDir) ? Session.ModsDir : _saved.ModsDir;
        ClaudePath = _saved.ClaudePath;
        GameSourcePath = _saved.GameSourcePath;
        SkipLoaderUi = _saved.SkipLoaderUi;
        SaveSlotsBuiltIn = _saved.SaveSlotsBuiltIn;
#if AI_RESOLVER
        ClaudeStatus = Session.ClaudeStatusText();
#endif
        if (ManagerStatus.Length == 0)
            ManagerStatus = _shell.ManagerVersion + (_shell.ManagerUpdate.Length > 0 ? $" · {_shell.ManagerUpdate.ToLowerInvariant()} available" : "");
        if (LoaderStatus.Length == 0)
            LoaderStatus = _shell.LoaderVersion + (_shell.LoaderUpdate.Length > 0 ? $" · {_shell.LoaderUpdate.ToLowerInvariant()} available" : "");
        Dirty = false;
        _loading = false;
    }

    partial void OnModsDirChanged(string value) => Touch();
    partial void OnClaudePathChanged(string value) => Touch();
    partial void OnGameSourcePathChanged(string value) => Touch();
    partial void OnSkipLoaderUiChanged(bool value) => Touch();
    partial void OnSaveSlotsBuiltInChanged(bool value) => Touch();
    private void Touch() { if (!_loading) Dirty = true; }

    [RelayCommand]
    private void Save()
    {
        var after = new SettingsValues(
            string.Equals(ModsDir, ManagerSession.SteamFallbackModsDir, StringComparison.OrdinalIgnoreCase) ? "" : ModsDir.Trim(),
            ClaudePath.Trim(), GameSourcePath.Trim(), SkipLoaderUi, SaveSlotsBuiltIn);
        var result = Session.ApplySettingsChange(_saved, after);
        if (result.RearmFolderWatcher) _shell.WatchModsFolder();
        Load();
        if (result.Saved) Session.Refresh();
    }

    [RelayCommand]
    private void Revert() => Load();

    [RelayCommand]
    private void BrowseMods()
    {
        var dlg = new OpenFolderDialog { Title = "Pick the mods folder inside your Road to Vostok install" };
        if (dlg.ShowDialog() == true) ModsDir = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseGameSource()
    {
        var dlg = new OpenFolderDialog { Title = "Pick the decompiled game folder (Decomp)" };
        if (dlg.ShowDialog() == true) GameSourcePath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseClaude()
    {
        var dlg = new OpenFileDialog { Title = "Pick the claude executable", Filter = "Programs (*.exe;*.cmd)|*.exe;*.cmd|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) ClaudePath = dlg.FileName;
    }

    [RelayCommand]
    private async Task RunSetup()
    {
        if (_shell.ShowSetupAsync != null) await _shell.ShowSetupAsync(SetupReason.FromSettings);
        Session.Refresh();
        Load();
    }

    [RelayCommand]
    private async Task CheckManager()
    {
        var s = await Session.CheckManagerUpdateAsync(forceFresh: true);
        ManagerStatus = s.Text;
        _shell.ManagerUpdate = s.IsOutdated ? $"Update to {s.Latest}" : "";
    }

    [RelayCommand]
    private async Task CheckLoader()
    {
        var s = await Session.CheckMmlVersionAsync(forceFresh: true);
        LoaderStatus = s.Text;
        _shell.LoaderUpdate = s.IsOutdated ? $"Update to {s.Latest}" : "";
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try { Process.Start("explorer.exe", $"\"{AppPaths.ManagerDir}\""); } catch { }
    }

    [RelayCommand]
    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
