// AI edition only: the Claude resolve of one file-overlap conflict.
#if AI_RESOLVER
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VostokModManager.Ai;
using VostokModManager.Domain;
using VostokModManager.Services;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>A mod the merged source can be written into.</summary>
public sealed partial class MergeTargetItem : ObservableObject
{
    public MergeTargetItem(ConflictResolver.ConflictMod mod) => Mod = mod;
    public ConflictResolver.ConflictMod Mod { get; }
    public string Name => string.IsNullOrEmpty(Mod.DisplayName) ? Mod.ModId : Mod.DisplayName;
    public string Label => Applied ? $"✓ Applied to {Name}" : $"Apply merged source to {Name}…";
    public string Tooltip => $"Replace the file inside {System.IO.Path.GetFileName(Mod.ArchivePath)} with the merged "
                             + "source. A timestamped .bak of the archive is kept beside it.";

    [ObservableProperty] private bool _applied;
    partial void OnAppliedChanged(bool value) => OnPropertyChanged(nameof(Label));
}

/// <summary>The resolution window: runs Claude on the conflict, then
/// shows the verdict, the suggested order, the merged source (with
/// per-mod Apply) and Claude's raw output.</summary>
public sealed partial class ResolutionViewModel : ObservableObject
{
    private readonly ManagerSession _session;
    private readonly ConflictDetector.Conflict _conflict;
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;

    public ResolutionViewModel(ManagerSession session, ConflictDetector.Conflict conflict)
    {
        _session = session;
        _conflict = conflict;
        var (title, _) = ManagerSession.DescribeConflict(conflict);
        Title = title;
        ModsText = string.Join(", ", session.ToConflictView(conflict).ModNames);
    }

    public string Title { get; }
    public string ModsText { get; }
    public string Intro => "Claude Code reads each mod's version of the file (and the game's original, when Settings "
                           + "points at the decompiled source) and says whether they can be merged, whether a load "
                           + "order settles it, or whether they truly clash. Nothing changes until you apply.";

    // ── state ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _notRun;
    [ObservableProperty] private string _notRunText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _note = "";

    /// <summary>True once a merge was written into a mod.</summary>
    public bool Applied { get; private set; }

    public ConflictResolver.Verdict? Verdict { get; private set; }

    // ── verdict ───────────────────────────────────────────────────────

    public bool Ok => Verdict?.Ok == true;
    public bool Failed => Verdict is { Ok: false };
    public string ErrorText => Verdict?.Error ?? "";

    public string VerdictText => Verdict == null ? ""
        : !Verdict.Ok ? "✗  Claude couldn't resolve it"
        : Verdict.Result switch
        {
            "merge_safe" => "✓  Merge safe",
            "order_resolves" => "⚠  A load order resolves it",
            "incompatible" => "✗  Incompatible",
            _ => "?  " + (string.IsNullOrEmpty(Verdict.Result) ? "No verdict" : Verdict.Result),
        };

    public string VerdictBrushKey => Verdict == null || !Verdict.Ok ? "B.SalmonText"
        : Verdict.Result switch
        {
            "merge_safe" => "B.Green",
            "order_resolves" => "B.AmberText",
            "incompatible" => "B.SalmonText",
            _ => "B.Text2",
        };

    public string VerdictWord => Verdict == null || string.IsNullOrEmpty(Verdict.Result) ? "" : Verdict.Result;
    public string ReasonText => Verdict == null ? "" : string.IsNullOrEmpty(Verdict.Reason) ? "(no reason provided)" : Verdict.Reason;
    public string CostText => Verdict == null ? "" : $"Cost: ${Verdict.CostUsd:F4}";

    public IReadOnlyList<string> LoadOrder => Verdict?.LoadOrder.Select((m, i) => $"{i + 1}.  {m}").ToList()
                                              ?? (IReadOnlyList<string>)Array.Empty<string>();
    public bool HasLoadOrder => Ok && LoadOrder.Count > 0;

    public bool IsMergeSafe => Ok && Verdict!.Result == "merge_safe";
    public bool HasMerge => IsMergeSafe && !string.IsNullOrEmpty(Verdict!.MergedSource);
    public string MergedSource => Verdict?.MergedSource ?? "";
    public ObservableCollection<MergeTargetItem> Targets { get; } = new();
    public bool NoArchiveTargets => HasMerge && Targets.Count == 0;

    public string RawText => Verdict == null ? ""
        : string.IsNullOrEmpty(Verdict.RawText) ? "(no output captured)" : Verdict.RawText;
    public bool HasRaw => Verdict != null;
    /// <summary>Open by default when the run failed: then it is the only
    /// thing to read.</summary>
    [ObservableProperty] private bool _rawExpanded;

    // ── run ───────────────────────────────────────────────────────────

    public async Task RunAsync()
    {
        IsWorking = true;
        _clock.Restart();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(),
            Application.Current.Dispatcher);
        Tick();
        _timer.Start();
        try
        {
            Verdict = await _session.ResolveAsync(_conflict);
        }
        catch (Exception ex)
        {
            Verdict = new ConflictResolver.Verdict { Ok = false, ConflictKey = _conflict.Key, Error = ex.Message };
        }
        finally
        {
            _timer.Stop();
            _clock.Stop();
            IsWorking = false;
        }

        if (Verdict == null)
        {
            // Busy, unsupported or Claude missing: the session said why on
            // the status line; say it here too.
            NotRun = true;
            NotRunText = _session.IsResolving
                ? "Another resolve is still running. Wait for it to finish, then try again."
                : _session.ResolveHint(_conflict) + "\n\n" + _session.ClaudeStatusText();
            return;
        }

        if (Verdict.Result == "merge_safe")
            foreach (var m in Verdict.Mods.Where(m => m.IsArchive))
                Targets.Add(new MergeTargetItem(m));
        RawExpanded = !Verdict.Ok;
        HasResult = true;
        OnPropertyChanged(string.Empty);
    }

    private void Tick()
    {
        var t = _clock.Elapsed;
        ElapsedText = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ── actions ───────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply(MergeTargetItem? target)
    {
        if (target == null || Verdict == null || target.Applied) return;
        if (_session.ApplyMergeToMod(Verdict, target.Mod))
        {
            target.Applied = true;
            Applied = true;
            Note = $"Merged source written into {target.Name}. The conflict list rescans on its own.";
        }
    }

    [RelayCommand]
    private void CopyMerged() => Copy(MergedSource, "Merged source copied.");

    [RelayCommand]
    private void CopyRaw() => Copy(RawText, "Claude's output copied.");

    private void Copy(string text, string done)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            Note = done;
        }
        catch (Exception ex)
        {
            Note = "Couldn't copy: " + ex.Message;
        }
    }
}
#endif
