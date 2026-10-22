using System.Windows.Controls;
using VostokModManager.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        vm.SelectedRows.Clear();
        vm.SelectedRows.AddRange(List.SelectedItems.OfType<LibraryRow>());
    }
}
