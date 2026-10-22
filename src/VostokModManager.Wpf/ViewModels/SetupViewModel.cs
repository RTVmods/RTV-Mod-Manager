using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One of the five steps, as the progress list shows it.</summary>
public sealed partial class SetupStep : ObservableObject
{
    public SetupStep(SetupStepId id, int number, string title, bool required)
    {
        Id = id;
        Number = number;
        Title = title;
        Required = required;
    }

    public SetupStepId Id { get; }
    public int Number { get; }
    public string Title { get; }
    public bool Required { get; }

    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _summary = "";

    public string Mark => IsDone ? "✓" : Number.ToString();
    public string Tag => IsDone ? "" : Required ? "REQUIRED" : "OPTIONAL";
    partial void OnIsDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(Mark));
        OnPropertyChanged(nameof(Tag));
    }
}

/// <summary>A loader profile offered for import in step 3.</summary>
public sealed partial class LoaderProfileChoice : ObservableObject
{
    public LoaderProfileChoice(LoaderProfileInfo info) => Info = info;
    public LoaderProfileInfo Info { get; }
    public string Label => $"{Info.Name}  ·  {Info.ModCount} mods";
    [ObservableProperty] private bool _import = true;
}

/// <summary>The setup window: five checks before the first launch. Each
/// step reads its state from the session and applies straight away;
/// nothing in the game folder changes before the user acts on a step.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ManagerSession _session;

    public SetupViewModel(ManagerSession session, SetupReason reason)
    {
        _session = session;
        Reason = reason;
        Steps = new ObservableCollection<SetupStep>
        {
            new(SetupStepId.GameFolder, 1, "Game folder", true),
            new(SetupStepId.ModLoader, 2, "Mod Loader", false),
            new(SetupStepId.Profiles, 3, "Mods and profiles", true),
            new(SetupStepId.SaveSlots, 4, "Save slots", false),
            new(SetupStepId.VostokMods, 5, "Update checks", false),
        };
        WantsBackup = session.IsFirstRun;
        Read();
        var firstOpen = Steps.FirstOrDefault(s => s.Required && !s.IsDone) ?? Steps[0];
        Open(firstOpen);
    }

    public SetupReason Reason { get; }
    public ObservableCollection<SetupStep> Steps { get; }
    public ObservableCollection<LoaderProfileChoice> LoaderProfiles { get; } = new();
    public ObservableCollection<string> DetectedFolders { get; } = new();

    [ObservableProperty] private SetupStep? _openStep;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _canFinish;

    // step 1
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _folderMessage = "";
    // step 2
    [ObservableProperty] private string _loaderText = "";
    [ObservableProperty] private bool _skipLauncher;
    [ObservableProperty] private bool _skipLauncherSupported;
    // step 3
    [ObservableProperty] private bool _profilesAdopted;
    [ObservableProperty] private string _profilesText = "";
    [ObservableProperty] private string _profileName = "Default";
    [ObservableProperty] private bool _keepLoaderProfiles = true;
    // step 4
    [ObservableProperty] private bool _saveSlotsOn;
    [ObservableProperty] private string _saveSlotsText = "";
    // step 5
    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private bool _linking;
    // finish
    [ObservableProperty] private bool _wantsBackup;
    [ObservableProperty] private bool _working;

    /// <summary>Raised when the window should close.</summary>
    public event Action? Done;

    public bool IsOpen(SetupStepId id) => OpenStep?.Id == id;

    [RelayCommand]
    private void Open(SetupStep step)
    {
        foreach (var s in Steps) s.IsOpen = ReferenceEquals(s, step);
        OpenStep = step;
    }

    /// <summary>Re-reads every step from the session.</summary>
    private void Read()
    {
        var folder = _session.GetGameFolderStatus();
        Folder = folder.Current.GameDirExists ? folder.Current.GameDir : _session.ModsDir;
        FolderMessage = folder.Current.Message;
        DetectedFolders.Clear();
        foreach (var d in folder.DetectedModsDirs) DetectedFolders.Add(d);
        Set(SetupStepId.GameFolder, folder.Current.Ok, folder.Current.Ok ? "Found" : "Not found yet");

        var loader = _session.GetModLoaderStatus();
        LoaderText = loader.Installed
            ? $"Mod Loader {loader.InstalledVersion} is installed in the game folder."
            : "The Mod Loader isn't installed in this game folder. Mods won't load until it is.";
        SkipLauncher = loader.SkipLoaderUi;
        SkipLauncherSupported = loader.SupportsSkipUi;
        Set(SetupStepId.ModLoader, loader.Installed, loader.Installed ? $"{loader.InstalledVersion} installed" : "Not installed");

        var profiles = _session.GetProfilesSetupStatus();
        ProfilesAdopted = profiles.Adopted;
        ProfileName = profiles.Offer.DefaultName;
        if (LoaderProfiles.Count == 0)
            foreach (var p in profiles.Offer.LoaderProfiles) LoaderProfiles.Add(new LoaderProfileChoice(p));
        ProfilesText = profiles.Adopted
            ? $"Profiles are in use. The active one is '{profiles.ActiveProfile}'."
            : $"Found {profiles.Offer.ModCount} mods"
              + (profiles.Offer.LoaderProfiles.Count > 0 ? $" and {profiles.Offer.LoaderProfiles.Count} more profile(s) from the Mod Loader." : ".");
        Set(SetupStepId.Profiles, profiles.Adopted || profiles.Declined,
            profiles.Adopted ? $"Active: {profiles.ActiveProfile}" : $"{profiles.Offer.ModCount} mods found");

        var slots = _session.GetSaveSlotsSetupStatus();
        SaveSlotsOn = slots.On;
        SaveSlotsText = slots.ArchiveAvailable
            ? "The game has one save. With slots on you can keep several, each tied to a profile; your current save becomes Slot 1."
            : "This build doesn't carry the Save Slots mod.";
        Set(SetupStepId.SaveSlots, slots.On, slots.On ? "On" : "Off");

        var vm = _session.GetVostokModsSetupStatus();
        LinkText = vm.Unlinked == 0
            ? $"All {vm.Linked} mods are linked to their VostokMods page, so update checks cover them."
            : $"{vm.Linked} mods are linked. {vm.Unlinked} aren't, so their updates can't be checked: "
              + string.Join(", ", vm.UnlinkedNames.Take(6)) + (vm.UnlinkedNames.Count > 6 ? " …" : "");
        Set(SetupStepId.VostokMods, vm.Unlinked == 0, vm.Unlinked == 0 ? "All linked" : $"{vm.Unlinked} not linked");

        var done = Steps.Count(s => s.IsDone);
        Progress = done / (double)Steps.Count;
        var requiredLeft = Steps.Count(s => s.Required && !s.IsDone);
        ProgressText = requiredLeft == 0 ? $"{done} of 5 done · ready to finish" : $"{done} of 5 done · {requiredLeft} required left";
        CanFinish = requiredLeft == 0;
    }

    private void Set(SetupStepId id, bool done, string summary)
    {
        var s = Steps.First(x => x.Id == id);
        s.IsDone = done;
        s.Summary = summary;
    }

    private void Next()
    {
        Read();
        var next = Steps.FirstOrDefault(s => !s.IsDone && s.Number > (OpenStep?.Number ?? 0))
                   ?? Steps.FirstOrDefault(s => !s.IsDone);
        if (next != null) Open(next);
    }

    // ── step actions ──────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Pick the Road to Vostok folder (or its mods folder)" };
        if (dlg.ShowDialog() == true) Folder = dlg.FolderName;
    }

    [RelayCommand]
    private void UseFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path)) Folder = path;
        var result = _session.ApplyGameFolderStep(Folder);
        if (result == null)
        {
            FolderMessage = ManagerSession.CheckGameFolder(Folder).Message;
            return;
        }
        Next();
    }

    partial void OnSkipLauncherChanged(bool value)
    {
        if (value != _session.Settings.SkipLoaderUi) _session.ApplyModLoaderStep(value);
    }

    [RelayCommand]
    private void AdoptProfiles()
    {
        var imports = KeepLoaderProfiles
            ? LoaderProfiles.Where(p => p.Import).Select(p => p.Info.Name).ToList()
            : new List<string>();
        Working = true;
        try
        {
            if (_session.AdoptProfileModel(ProfileName, imports) != null) Next();
        }
        finally { Working = false; }
    }

    [RelayCommand]
    private void DeclineProfiles()
    {
        _session.DeclineProfileMigration();
        Next();
    }

    partial void OnSaveSlotsOnChanged(bool value)
    {
        if (value == _session.Settings.SaveSlotsBuiltIn) return;
        _session.ApplySaveSlotsStep(value);
        Read();
    }

    [RelayCommand]
    private async Task LinkMods()
    {
        Linking = true;
        try { await _session.AutoLinkModsAsync(); }
        catch (Exception ex) { _session.Prompt.Error("Couldn't reach VostokMods", ex); }
        finally { Linking = false; }
        Read();
    }

    [RelayCommand]
    private async Task Finish()
    {
        Working = true;
        try
        {
            if (WantsBackup) await _session.WriteFirstRunBackupAsync();
            if (_session.IsFirstRun) _session.FinishFirstRun();
        }
        finally { Working = false; }
        Done?.Invoke();
    }

    [RelayCommand]
    private void Skip()
    {
        if (_session.IsFirstRun) _session.FinishFirstRun();
        Done?.Invoke();
    }
}
