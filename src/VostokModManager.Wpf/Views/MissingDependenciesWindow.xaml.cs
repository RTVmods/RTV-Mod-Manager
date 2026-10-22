using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Dependencies freshly installed mods declare that are neither
/// installed nor in the library: paste a VostokMods URL or slug per row
/// and download. Closing is held while a download runs, so the caller
/// sees every install when the window returns.</summary>
public partial class MissingDependenciesWindow : Window
{
    private readonly MissingDependenciesViewModel _vm;

    public MissingDependenciesWindow(MissingDependenciesViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.Log.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) LogScroll.ScrollToEnd();
        };
        Closed += (_, _) => _vm.Dispose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>The `showResolver` callback for
    /// <c>session.CheckAndPromptMissingDepsAsync(ids, showResolver)</c>:
    /// shows this window over `owner()` and completes when it closes.</summary>
    public static Func<MissingDependencyResolver, Task> Resolver(Func<Window?> owner)
        => resolver =>
        {
            var w = new MissingDependenciesWindow(new MissingDependenciesViewModel(resolver));
            PromptOwner.Attach(w, owner());
            w.ShowDialog();
            return Task.CompletedTask;
        };

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // A download in flight finishes first.
        if (_vm.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
