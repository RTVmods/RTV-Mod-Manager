using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VostokModManager.Wpf.Views;

public partial class SavesView : UserControl
{
    public SavesView() => InitializeComponent();

    /// <summary>The ⋯ button opens its menu on a left click.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
