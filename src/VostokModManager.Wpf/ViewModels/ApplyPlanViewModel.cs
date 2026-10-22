using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A Source choice with the words the plan shows for it.</summary>
public sealed record ApplySourceOption(ApplySource Source, string Label)
{
    public override string ToString() => Label;

    public static ApplySourceOption For(ApplySource s) => new(s, s switch
    {
        ApplySource.KeepCurrent => "Keep installed",
        ApplySource.UseBundle   => "Profile bundle",
        ApplySource.UseLibrary  => "Library copy",
        ApplySource.Download    => "VostokMods",
        ApplySource.Locked      => "Locked",
        _                       => "Skip",
    });
}

/// <summary>One mod of an Apply plan: a two-way tick and a source choice.</summary>
public sealed partial class ApplyRowItem : ObservableObject
{
    private bool _syncing;

    public ApplyRowItem(ApplyPlanRow row)
    {
        Row = row;
        SourceChoices = row.SourceChoices.Select(ApplySourceOption.For).ToList();
        _source = SourceChoices.FirstOrDefault(o => o.Source == row.Source) ?? SourceChoices[0];
        _apply = row.Apply;
    }

    public ApplyPlanRow Row { get; }
    public string Label => string.IsNullOrEmpty(Row.ProfileMod.DisplayName) ? Row.ProfileMod.ModId : Row.ProfileMod.DisplayName;
    public string Version => string.IsNullOrEmpty(Row.ProfileMod.Version) ? "" : "v" + Row.ProfileMod.Version;
    public string Installed => Row.Installed == null ? "not installed"
        : string.IsNullOrEmpty(Row.Installed.Version) ? "installed" : $"installed v{Row.Installed.Version}";
    public IReadOnlyList<ApplySourceOption> SourceChoices { get; }
    public bool SourceIsChangeable => !Row.SourceIsFixed;
    public bool IsCheckable => Row.IsCheckable;
    public string StatusIcon => Row.StatusIcon;
    public string StatusText => Row.StatusText;
    public string SourceName => $"Source for {Label}";

    [ObservableProperty] private bool _apply;
    [ObservableProperty] private ApplySourceOption _source;

    partial void OnApplyChanged(bool value)
    {
        if (!_syncing) Row.Apply = value;
    }

    partial void OnSourceChanged(ApplySourceOption value)
    {
        if (_syncing || value == null) return;
        // The row re-derives what it does and ticks itself when the new
        // source acts on a file.
        Row.Source = value.Source;
        Sync();
    }

    /// <summary>Re-reads the row after a source change or Select all / none.</summary>
    public void Sync()
    {
        _syncing = true;
        Apply = Row.Apply;
        _syncing = false;
        OnPropertyChanged(nameof(IsCheckable));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>The Apply plan window: what making a profile live in the mods
/// folder does per mod, the ticks and sources, and the run with its log.</summary>
public sealed partial class ApplyPlanViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private readonly ApplyPlan _plan;
    private CancellationTokenSource? _cts;

    public ApplyPlanViewModel(ManagerSession session, ApplyPlan plan)
    {
        _session = session;
        _plan = plan;
        foreach (var r in plan.Rows) Rows.Add(new ApplyRowItem(r));
    }

    public string Title => $"Apply '{_plan.Profile.Name}'";
    public string Summary => _plan.Summary;
    public string Warning =>
        "Mods in the live folder that aren't in this profile are taken out (their archives stay in the Library). "
        + "Locked mods are left as they are.";
    public ObservableCollection<ApplyRowItem> Rows { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _finished;
    [ObservableProperty] private bool _hasLog;

    public bool CanRun => !IsRunning && !Finished;
    public string RunLabel => IsRunning ? "Applying…" : Finished ? "Done" : "Apply";
    public string CloseLabel => IsRunning ? "Stop" : Finished ? "Close" : "Cancel";
    partial void OnIsRunningChanged(bool value) => Labels();
    partial void OnFinishedChanged(bool value) => Labels();
    private void Labels()
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(CloseLabel));
    }

    /// <summary>The outcome, once Run has finished.</summary>
    public ApplyResult? Result { get; private set; }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in _plan.Rows) if (r.IsCheckable) r.Apply = true;
        Sync();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in _plan.Rows) r.Apply = false;
        Sync();
    }

    private void Sync() { foreach (var r in Rows) r.Sync(); }

    [RelayCommand]
    private async Task Run()
    {
        IsRunning = true;
        HasLog = true;
        _cts = new CancellationTokenSource();
        try
        {
            var log = new Progress<string>(line => Log.Add(line));
            Result = await _session.ExecuteApplyAsync(_plan, log, _cts.Token);
            if (!Result.Applied) Log.Add("Stopped. Some changes may already have been made.");
        }
        catch (OperationCanceledException)
        {
            Log.Add("Stopped. Some changes may already have been made.");
        }
        catch (Exception ex)
        {
            Log.Add("Apply failed: " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            Finished = true;
            _cts.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
