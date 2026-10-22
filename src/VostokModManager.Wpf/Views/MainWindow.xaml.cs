using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Services.WindowPlacement.Restore(this);
        StateChanged += (_, _) => SyncMaximized();
        Loaded += (_, _) => SyncMaximized();
        DataContextChanged += (_, _) => Hook();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void Hook()
    {
        if (Shell == null) return;
        Shell.ShowSetupAsync = reason =>
        {
            var w = new SetupWindow(new SetupViewModel(Shell.Session, reason)) { Owner = this };
            w.ShowDialog();
            return Task.CompletedTask;
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Shell?.Shutdown();
        Services.WindowPlacement.Save(this);
        base.OnClosing(e);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>A maximized chromeless window overhangs the screen by the
    /// resize border; pad it back in, and swap the maximize glyph.</summary>
    private void SyncMaximized()
    {
        var max = WindowState == WindowState.Maximized;
        Frame.BorderThickness = max ? new Thickness(7) : new Thickness(0);
        MaxIcon.Data = (Geometry)FindResource(max ? "Icon.Restore" : "Icon.Max");
    }
}
