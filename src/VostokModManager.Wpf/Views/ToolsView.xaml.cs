using System.Windows;
using System.Windows.Controls;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

    private void OnPackagerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPackagerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is ToolsViewModel vm)
        {
            vm.Packager.Drop(files);
            e.Handled = true;
        }
    }
}
