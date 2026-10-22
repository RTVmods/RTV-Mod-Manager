using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>Revert a mod to an earlier version: every snapshot on disk,
/// newest first; Revert puts the selected one back, Delete removes it.</summary>
public sealed partial class RevertViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private readonly ModEntry _mod;

    public RevertViewModel(ManagerSession session, ModEntry mod)
    {
        _session = session;
        _mod = mod;
        Name = string.IsNullOrEmpty(mod.DisplayName) ? mod.ModId : mod.DisplayName;
        CurrentVersion = string.IsNullOrEmpty(mod.Version) ? "?" : "v" + mod.Version;
        Load();
    }

    public string Name { get; }
    public string CurrentVersion { get; }

    public ObservableCollection<ModBackupView> Backups { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand), nameof(DeleteCommand))]
    private ModBackupView? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand), nameof(DeleteCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageIsError;

    public bool IsEmpty => Backups.Count == 0;
    public bool HasBackups => Backups.Count > 0;

    public string Summary => IsEmpty
        ? $"Current: {CurrentVersion}. No backups on disk."
        : $"Current: {CurrentVersion}.   {Backups.Count} backup{(Backups.Count == 1 ? "" : "s")} on disk. "
          + "Reverting copies the chosen snapshot over the live .vmz; the current version is backed up "
          + "first, so a revert can itself be undone.";

    public string EmptyText =>
        "There are no earlier versions of this mod. The manager keeps a copy of a mod each time it updates "
        + "or reverts it, so a backup shows up here after the next update.";

    /// <summary>The revert's outcome, once one ran.</summary>
    public ModUpdateResult? Result { get; private set; }

    /// <summary>Set by the window: closes it after a successful revert.</summary>
    public Action? Done { get; set; }

    private void Load()
    {
        var keep = Selected?.Path;
        Backups.Clear();
        foreach (var b in _session.GetBackups(_mod)) Backups.Add(b);
        Selected = Backups.FirstOrDefault(b => b.Path == keep) ?? Backups.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(Summary));
    }

    private bool CanAct() => Selected != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Revert()
    {
        if (Selected is not { } b) return;
        IsBusy = true;
        Message = $"Reverting to {b.VersionLabel} …";
        MessageIsError = false;
        try
        {
            Result = await _session.RevertModFromBackupAsync(_mod, b.Path);
        }
        catch (Exception ex)
        {
            Result = new ModUpdateResult(_mod.ModId, Name, false, _mod.Version, _mod.Version, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (Result == null)
        {
            Message = "That backup is gone from disk; the list was re-read.";
            MessageIsError = true;
            Load();
            return;
        }
        if (Result.Ok)
        {
            Done?.Invoke();
            return;
        }
        Message = Result.Message;
        MessageIsError = true;
        Load();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Delete()
    {
        if (Selected is not { } b) return;
        if (!_session.DeleteBackup(b)) return;
        Message = $"Deleted the {b.VersionLabel} backup ({b.SizeLabel}).";
        MessageIsError = false;
        Load();
    }
}
