using System.Collections.Specialized;
using System.Windows;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class ApplyPlanWindow : Window
{
    private readonly ApplyPlanViewModel _vm;

    public ApplyPlanWindow(ApplyPlanViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = vm.Title;
        // Keep the newest log line in view while the apply runs.
        vm.Log.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) LogScroll.ScrollToEnd();
        };
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning) _vm.Cancel();
        else Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Don't leave half an apply running behind a closed window.
        if (_vm.IsRunning) { _vm.Cancel(); e.Cancel = true; }
        base.OnClosing(e);
    }
}
