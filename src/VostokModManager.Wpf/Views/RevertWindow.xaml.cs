using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Every backup of a mod with version, date and size; revert
/// to one or delete it.</summary>
public partial class RevertWindow : Window
{
    private readonly RevertViewModel _vm;

    public RevertWindow(RevertViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = "Revert — " + vm.Name;
        vm.Done = Close;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Opens the revert list for `mod` over `owner`; completes
    /// when it closes (after a successful revert, or Close).</summary>
    public static Task ShowAsync(Window owner, ManagerSession session, ModEntry mod)
    {
        var w = new RevertWindow(new RevertViewModel(session, mod));
        PromptOwner.Attach(w, owner);
        using (PromptOwner.Use(session, w)) w.ShowDialog();
        return Task.CompletedTask;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click commits the revert, as the button does.
        if (e.OriginalSource is FrameworkElement { DataContext: ModBackupView } && _vm.RevertCommand.CanExecute(null))
            _vm.RevertCommand.Execute(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // A revert in flight finishes first.
        if (_vm.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
