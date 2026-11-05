using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>A likely cause, ticked when it can be turned off.</summary>
public sealed partial class CrashSuspectItem : ObservableObject
{
    public CrashSuspectItem(CrashSuspect suspect)
    {
        Suspect = suspect;
        _isChecked = suspect.CanTurnOff && suspect.Likely;
    }

    public CrashSuspect Suspect { get; }
    [ObservableProperty] private bool _isChecked;

    public string CheckTip => Suspect.CanTurnOff
        ? "Ticked: “Turn off” turns this mod off"
        : "Locked or already off: “Turn off” leaves it as it is";
}

/// <summary>A way forward; the first one is the suggested one.</summary>
public sealed record CrashStepItem(CrashStep Step, bool IsFirst);

/// <summary>What the game crash looks like to the manager: the likely
/// causes, the ways forward, what changed since the last clean session
/// and the log's last lines. A step's button closes the window and hands
/// the step back for the shell to carry out.</summary>
public partial class CrashReportWindow : Window
{
    private CrashAction? _action;

    public CrashReportWindow(CrashReport report)
    {
        InitializeComponent();
        Report = report;
        Suspects = report.Suspects.Select(s => new CrashSuspectItem(s)).ToList();
        Steps = report.Steps.Select((s, i) => new CrashStepItem(s, i == 0)).ToList();
        ChangesTitle = string.IsNullOrEmpty(report.LastGoodAge)
            ? "CHANGES SINCE YOUR LAST CLEAN SESSION"
            : $"CHANGES SINCE YOUR LAST CLEAN SESSION ({report.LastGoodAge.ToUpperInvariant()})";
        TailTitle = report.Log.HasCrashSignature ? "THE LOG'S LAST LINES BEFORE THE CRASH" : "THE LOG'S LAST LINES";
        var lines = new List<string>(report.Log.Tail);
        if (report.Log.HasCrashSignature)
        {
            lines.Add("");
            lines.Add(report.Log.CrashLine);
            lines.AddRange(report.Log.Backtrace.Take(6));
        }
        TailText = string.Join("\n", lines);
        DataContext = this;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        // Focus lands on the first checkbox and scrolls the heading away; start at the top.
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => Scroller.ScrollToHome(),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    public CrashReport Report { get; }
    public IReadOnlyList<CrashSuspectItem> Suspects { get; }
    public IReadOnlyList<CrashStepItem> Steps { get; }
    public string ChangesTitle { get; }
    public string TailTitle { get; }
    public string TailText { get; }

    /// <summary>Shows the report over `owner`. Returns the step picked
    /// (null when closed) and the ticked suspects' mod ids.</summary>
    public static (CrashAction? Action, IReadOnlyList<string> ModIds) Show(Window? owner, CrashReport report)
    {
        var w = new CrashReportWindow(report);
        PromptOwner.Attach(w, owner);
        w.ShowDialog();
        var ids = w.Suspects.Where(s => s.IsChecked && s.Suspect.CanTurnOff).Select(s => s.Suspect.ModId).ToList();
        return (w._action, ids);
    }

    private void OnStep(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CrashStepItem item) return;
        if (item.Step.Action == CrashAction.TurnOffSuspects && !Suspects.Any(s => s.IsChecked && s.Suspect.CanTurnOff))
        {
            new DialogPrompt { Owner = this }.Inform("Turn off", "Tick the mods to turn off under Likely causes first.");
            return;
        }
        _action = item.Step.Action;
        Close();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var path = Report.Log.LogPath;
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
