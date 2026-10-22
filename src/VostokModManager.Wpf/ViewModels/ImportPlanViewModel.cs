using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>One entry of an import plan, with a two-way tick.</summary>
public sealed partial class ImportRowItem : ObservableObject
{
    public ImportRowItem(ImportPlanRow row)
    {
        Row = row;
        _apply = row.Apply;
    }

    public ImportPlanRow Row { get; }
    public string Label => Row.Label;
    public string Via => Row.Via;
    public string Version => string.IsNullOrEmpty(Row.Entry.Version) ? "" : "v" + Row.Entry.Version;
    public string OptionalTag => Row.IsOptional ? "  optional" : "";
    public string StatusIcon => Row.StatusIcon;
    public string StatusText => Row.StatusText;
    public bool IsCheckable => Row.IsCheckable;

    [ObservableProperty] private bool _apply;
    partial void OnApplyChanged(bool value) => Row.Apply = value;

    /// <summary>Re-reads the tick after a Select all / none on the plan.</summary>
    public void Sync() => Apply = Row.Apply;
}

/// <summary>The import plan window: what importing a list or modpack
/// will do per mod, the ticks, and the run with its log.</summary>
public sealed partial class ImportPlanViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private readonly ImportPlan _plan;
    private CancellationTokenSource? _cts;

    public ImportPlanViewModel(ManagerSession session, ImportPlan plan)
    {
        _session = session;
        _plan = plan;
        foreach (var r in plan.Rows) Rows.Add(new ImportRowItem(r));
    }

    public string Title => _plan.Title;
    public string Summary => _plan.Summary;
    public ObservableCollection<ImportRowItem> Rows { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _finished;
    [ObservableProperty] private bool _hasLog;

    public bool CanRun => !IsRunning && !Finished;
    public string RunLabel => IsRunning ? "Importing…" : Finished ? "Done" : "Import";
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
    public ImportResult? Result { get; private set; }

    [RelayCommand] private void SelectAll() { _plan.SelectAll(); Sync(); }
    [RelayCommand] private void SelectNone() { _plan.SelectNone(); Sync(); }
    [RelayCommand] private void SelectRequired() { _plan.SelectRequiredOnly(); Sync(); }
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
            Result = await _session.ExecuteImportAsync(_plan, log, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Add("Import failed: " + ex.Message);
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
