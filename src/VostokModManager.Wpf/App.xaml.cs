using System.IO;
using System.Windows;
using System.Windows.Threading;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf;

public partial class App : Application
{
    /// <summary>One manager at a time: two would race on mod_config.cfg
    /// and the mods folder. The WinForms build never guarded this.</summary>
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\VostokModManager.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Vostok Mod Manager is already running.", "Vostok Mod Manager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

#if DEBUG
        // `--screenshot <page> <out.png>` renders one page and exits, so
        // the look can be compared with the mockup without clicking.
        var shot = ScreenshotRequest.Parse(e.Args);
#endif

        var prompt = new DialogPrompt();
        var session = ManagerSession.Open(prompt);
        var shell = new ShellViewModel(session);
        var window = new MainWindow { DataContext = shell };
        prompt.Owner = window;
        MainWindow = window;
        window.Show();

#if DEBUG
        if (shot != null)
        {
            _ = ScreenshotRequest.RunAsync(shot, window, shell);
            return;
        }
#endif
        _ = shell.StartAsync();
    }

    /// <summary>An exception nobody caught: tell the user, keep running
    /// when it's safe to, and leave a crash log next to settings.json.</summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var log = Path.Combine(AppPaths.ManagerDir, "crash.log");
            Directory.CreateDirectory(AppPaths.ManagerDir);
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { /* the dialog below still tells the user */ }
        MessageBox.Show(MainWindow, e.Exception.Message + "\n\nDetails were written to crash.log in the manager's settings folder.",
            "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { /* not owned */ }
        _single?.Dispose();
        base.OnExit(e);
    }
}
