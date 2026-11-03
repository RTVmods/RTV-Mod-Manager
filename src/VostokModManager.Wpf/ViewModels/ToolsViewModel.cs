using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>The Tools page: log analysis and the support package, each
/// a tab.</summary>
public sealed partial class ToolsViewModel : PageViewModel
{
    public ToolsViewModel(ManagerSession session) : base(session) { }

    [ObservableProperty] private string _tab = "log";

    public override void OnShown()
    {
        if (Logs.Count == 0) LoadLogs();
        SupportSummary = Session.GetSupportPackageSummary().Text;
    }

    [RelayCommand]
    private void SetTab(string key) => Tab = key;

    // ── log analysis ──────────────────────────────────────────────────

    public ObservableCollection<LogFileItem> Logs { get; } = new();
    [ObservableProperty] private LogFileItem? _selectedLog;
    [ObservableProperty] private LogAnalysisReport? _report;
    [ObservableProperty] private bool _hasLogs = true;

    partial void OnSelectedLogChanged(LogFileItem? value)
    {
        Report = value == null ? null : Session.AnalyzeLog(value.Path);
    }

    [RelayCommand]
    private void LoadLogs()
    {
        Logs.Clear();
        var logs = Session.AvailableLogFiles();
        foreach (var l in logs) Logs.Add(l);
        HasLogs = Logs.Count > 0;
        var i = ManagerSession.PreferredLogIndex(logs);
        SelectedLog = i >= 0 ? Logs[i] : null;
    }

    // ── Claude's reading of the log (AI edition) ──────────────────────

#if AI_RESOLVER
    public bool IsAiEdition => true;
#else
    public bool IsAiEdition => false;
#endif

    [ObservableProperty] private bool _diagnosing;
    [ObservableProperty] private string _diagnosis = "";
    public ObservableCollection<string> DiagnosisIssues { get; } = new();
    public ObservableCollection<string> ProposedOrder { get; } = new();
    private List<ProposedPriorityChange> _proposed = new();
    [ObservableProperty] private bool _hasProposedOrder;

    [RelayCommand]
    private async Task AnalyzeWithClaude()
    {
#if AI_RESOLVER
        if (Report == null || Diagnosing) return;
        Diagnosing = true;
        Diagnosis = "Claude is reading the log…";
        DiagnosisIssues.Clear();
        ProposedOrder.Clear();
        try
        {
            var verdict = await Session.DiagnoseLogAsync(Report.Analysis);
            if (!verdict.Ok)
            {
                Diagnosis = string.IsNullOrEmpty(verdict.Error) ? "Claude couldn't finish." : verdict.Error;
                return;
            }
            Diagnosis = verdict.Summary;
            foreach (var i in verdict.Issues)
                DiagnosisIssues.Add($"{i.Title}\n{i.Cause}\nFix: {i.Fix}" + (i.Mods.Count > 0 ? $"\nMods: {string.Join(", ", i.Mods)}" : ""));
            var rows = Session.ProposedOrderRows(verdict);
            foreach (var r in rows.Where(r => r.Changes))
                ProposedOrder.Add($"{r.Name}:  {(r.CurrentPriority?.ToString() ?? "not installed")}  →  {r.ProposedPriority}");
            _proposed = rows.Where(r => r.Changes).Select(r => new ProposedPriorityChange(r.ModId, r.ProposedPriority)).ToList();
            HasProposedOrder = _proposed.Count > 0;
        }
        finally { Diagnosing = false; }
#else
        await Task.CompletedTask;
#endif
    }

    [RelayCommand]
    private void ApplyProposedOrder()
    {
        if (_proposed.Count == 0) return;
        if (Session.ApplyProposedOrder(_proposed) > 0)
        {
            HasProposedOrder = false;
            ProposedOrder.Clear();
        }
    }

    // ── support package ───────────────────────────────────────────────

    [ObservableProperty] private string _supportSummary = "";
    [ObservableProperty] private bool _includeRootLogs = true;
    /// <summary>Bundle the mod archives too. Off by default: a big
    /// modlist makes a package of several GB.</summary>
    [ObservableProperty] private bool _includeModFiles;
    [ObservableProperty] private string _supportProgress = "";
    [ObservableProperty] private int _supportPercent;

    [RelayCommand]
    private async Task BuildSupportPackage()
    {
        var summary = Session.GetSupportPackageSummary();
        var dlg = new SaveFileDialog
        {
            Title = "Save support package",
            FileName = summary.SuggestedFileName,
            Filter = "Support package (*.vmzlog)|*.vmzlog|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var progress = new Progress<(string Message, int Percent)>(p =>
        {
            SupportProgress = p.Message;
            SupportPercent = p.Percent;
        });
        var outcome = await Session.BuildSupportPackageAsync(dlg.FileName, IncludeRootLogs, progress, IncludeModFiles);
        if (outcome?.RevealRequested == true)
        {
            try { Process.Start("explorer.exe", $"/select,\"{outcome.Result.OutputPath}\""); } catch { }
        }
    }
}
