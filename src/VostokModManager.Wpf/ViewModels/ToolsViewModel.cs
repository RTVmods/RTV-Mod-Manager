using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>The Tools page: log analysis, the Mod Packager and the
/// support package, each a tab.</summary>
public sealed partial class ToolsViewModel : PageViewModel
{
    public ToolsViewModel(ManagerSession session) : base(session)
    {
        Packager = new PackagerViewModel(session);
    }

    [ObservableProperty] private string _tab = "log";

    public PackagerViewModel Packager { get; }

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
        var outcome = await Session.BuildSupportPackageAsync(dlg.FileName, IncludeRootLogs, progress);
        if (outcome?.RevealRequested == true)
        {
            try { Process.Start("explorer.exe", $"/select,\"{outcome.Result.OutputPath}\""); } catch { }
        }
    }
}

/// <summary>A dependency row in the packager with two-way ticks.</summary>
public sealed partial class PackagerDepItem : ObservableObject
{
    private readonly PackagerViewModel _owner;

    public PackagerDepItem(PackagerViewModel owner, PackagerDepView view)
    {
        _owner = owner;
        Row = view.Row;
        _required = view.Required;
        _optional = view.Optional;
    }

    public PackagerDepRow Row { get; }

    [ObservableProperty] private bool _required;
    [ObservableProperty] private bool _optional;

    partial void OnRequiredChanged(bool value) => _owner.Tick(this, required: true, value);
    partial void OnOptionalChanged(bool value) => _owner.Tick(this, required: false, value);
}

/// <summary>The Mod Packager form: build a .vmz with a proper mod.txt
/// (id, version, VostokMods source, dependencies), export a mod list or
/// build a modpack zip.</summary>
public sealed partial class PackagerViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private PackagerService _svc;
    private bool _syncing;

    public PackagerViewModel(ManagerSession session)
    {
        _session = session;
        _svc = session.OpenPackager();
        _svc.Logged += line => Log.Add(line);
        Pull();
    }

    public ObservableCollection<PackagerDepItem> Dependencies { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private string _modId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _priority = "0";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _dependencyFilter = "";
    [ObservableProperty] private string _customRequired = "";
    [ObservableProperty] private string _customOptional = "";

    partial void OnDependencyFilterChanged(string value)
    {
        if (_syncing) return;
        _svc.State.DependencyFilter = value;
        FillDependencies();
    }

    /// <summary>Copies the form into the service's state.</summary>
    private void Push()
    {
        var s = _svc.State;
        s.SourcePath = SourcePath;
        s.OutputPath = OutputPath;
        s.ModId = ModId;
        s.Name = Name;
        s.Version = Version;
        s.Priority = int.TryParse(Priority, out var p) ? p : 0;
        s.SourceText = SourceText;
        s.Author = Author;
        s.Description = Description;
        s.CustomRequired = CustomRequired;
        s.CustomOptional = CustomOptional;
    }

    /// <summary>Copies the service's state into the form.</summary>
    private void Pull()
    {
        _syncing = true;
        var s = _svc.State;
        SourcePath = s.SourcePath;
        OutputPath = s.OutputPath;
        ModId = s.ModId;
        Name = s.Name;
        Version = s.Version;
        Priority = s.Priority.ToString();
        SourceText = s.SourceText;
        Author = s.Author;
        Description = s.Description;
        CustomRequired = s.CustomRequired;
        CustomOptional = s.CustomOptional;
        DependencyFilter = s.DependencyFilter;
        _syncing = false;
        FillDependencies();
    }

    private void FillDependencies()
    {
        Dependencies.Clear();
        foreach (var v in _svc.VisibleDependencyRows()) Dependencies.Add(new PackagerDepItem(this, v));
    }

    internal void Tick(PackagerDepItem item, bool required, bool on)
    {
        if (_syncing) return;
        if (required) _svc.SetRequired(item.Row.ModId, on);
        else _svc.SetOptional(item.Row.ModId, on);
        // The two ticks exclude each other; re-read both.
        _syncing = true;
        item.Required = _svc.State.CheckedRequired.Contains(item.Row.ModId);
        item.Optional = _svc.State.CheckedOptional.Contains(item.Row.ModId);
        _syncing = false;
    }

    /// <summary>A file or folder dropped on the form.</summary>
    public void Drop(IEnumerable<string> paths)
    {
        var p = PackagerService.PickDropPath(paths);
        if (p == null) return;
        Push();
        _svc.Drop(p);
        Pull();
    }

    [RelayCommand]
    private void BrowseArchive()
    {
        var dlg = new OpenFileDialog { Title = "Pick the mod to package", Filter = "Mod archives (*.vmz;*.zip)|*.vmz;*.zip" };
        if (dlg.ShowDialog() != true) return;
        Push();
        _svc.OpenSource(dlg.FileName);
        Pull();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Pick the mod's folder (the one holding mod.txt)" };
        if (dlg.ShowDialog() != true) return;
        Push();
        _svc.OpenSource(dlg.FolderName);
        Pull();
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        Push();
        var dlg = new SaveFileDialog
        {
            Title = "Save the packed mod as",
            FileName = _svc.SuggestedOutputFileName(),
            InitialDirectory = _svc.SourceDirectory(),
            Filter = "Mod archive (*.vmz)|*.vmz",
        };
        if (dlg.ShowDialog() != true) return;
        OutputPath = dlg.FileName;
    }

    [RelayCommand]
    private void ReloadManifest()
    {
        Push();
        _svc.ReloadManifest();
        Pull();
    }

    [RelayCommand]
    private void Build()
    {
        Push();
        var problems = _svc.Validate();
        if (problems.Count > 0)
        {
            _session.Prompt.Inform("Can't build yet", string.Join("\n", problems), PromptKind.Warning);
            return;
        }
        _svc.Build();
        Pull();
    }

    [RelayCommand]
    private void ExportList()
    {
        Push();
        var problems = _svc.Validate(PackagerOutput.PackJson);
        if (problems.Count > 0)
        {
            _session.Prompt.Inform("Can't export yet", string.Join("\n", problems), PromptKind.Warning);
            return;
        }
        var dlg = new SaveFileDialog { Title = "Export mod list", FileName = _svc.SuggestedExportFileName(), Filter = "Mod list (*.json)|*.json" };
        if (dlg.ShowDialog() == true) _svc.ExportPackJson(dlg.FileName);
    }

    [RelayCommand]
    private void BuildModpack()
    {
        Push();
        var problems = _svc.Validate(PackagerOutput.Modpack);
        if (problems.Count > 0)
        {
            _session.Prompt.Inform("Can't build the modpack yet", string.Join("\n", problems), PromptKind.Warning);
            return;
        }
        var dlg = new SaveFileDialog { Title = "Save modpack", FileName = _svc.SuggestedModpackFileName(), Filter = "Modpack (*.zip)|*.zip" };
        if (dlg.ShowDialog() == true) _svc.BuildModpack(dlg.FileName);
    }

    [RelayCommand]
    private void OpenList()
    {
        var dlg = new OpenFileDialog { Title = "Open a mod list", Filter = "Mod list (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        Push();
        _svc.LoadPackJson(dlg.FileName);
        Pull();
    }

    [RelayCommand]
    private void Reset()
    {
        _svc = _session.OpenPackager();
        _svc.Logged += line => Log.Add(line);
        Log.Clear();
        Pull();
    }
}
