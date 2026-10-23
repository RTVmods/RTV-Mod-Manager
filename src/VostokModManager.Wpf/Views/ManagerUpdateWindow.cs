using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>The manager self-update's progress: the update prompt's look,
/// kept on screen with a progress bar while the new build downloads and
/// unpacks, until the manager closes for the restart. The main window is
/// disabled meanwhile, and the window can't be closed half-way.</summary>
internal static class ManagerUpdateWindow
{
    public static ManagerUpdateRunner Runner(Func<Window?> owner, ManagerSession session) => async update =>
    {
        var main = owner();
        var bar = new ProgressBar
        {
            Height = 3, Minimum = 0, Maximum = 1, IsIndeterminate = true, BorderThickness = new Thickness(0),
        };
        var detail = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var w = MessageWindow.Create(main, "Updating the mod manager",
            "Getting the new version. The manager closes and reopens by itself when it's ready.",
            PromptKind.Info, Array.Empty<string>(),
            extra: new StackPanel { Children = { bar, detail } });
        bar.Background = (Brush)w.FindResource("B.Hairline");
        bar.Foreground = (Brush)w.FindResource("B.Primary");
        detail.Style = (Style)w.FindResource("T.Muted");
        w.Kind.Text = "UPDATING";

        var running = true;
        w.Closing += (_, e) => e.Cancel = running;
        var progress = new Progress<ManagerUpdateProgress>(p =>
        {
            bar.IsIndeterminate = p.Fraction == null;
            bar.Value = p.Fraction ?? 0;
            detail.Text = p.Total > 0 ? $"{p.Step}  {Mb(p.Done)} of {Mb(p.Total)}" : p.Step;
        });

        if (main != null) main.IsEnabled = false;
        w.Show();
        ManagerUpdateResult? result = null;
        try
        {
            using (PromptOwner.Use(session, w)) result = await update(progress);
            return result;
        }
        finally
        {
            if (main != null) main.IsEnabled = true;
            running = false;
            // On a restart the window stays up until the app shuts down.
            if (result is not { MustExit: true }) w.Close();
        }
    };

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";
}
