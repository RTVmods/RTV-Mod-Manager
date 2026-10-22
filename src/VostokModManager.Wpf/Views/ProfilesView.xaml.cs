using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    /// <summary>A card's ⋯ button opens its menu on a left click.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>A matrix row's ⋯ button: the copy / remove menu, built
    /// from the row's actions (how many depends on the row).</summary>
    private void OnRowMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MatrixRowItem row } b || row.Actions.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        foreach (var a in row.Actions)
        {
            if (a.IsSeparator) menu.Items.Add(new Separator());
            else if (a.Command == null) menu.Items.Add(new MenuItem { Header = a.Header, IsEnabled = false });
            else menu.Items.Add(new MenuItem { Header = a.Header, Command = a.Command });
        }
        b.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>The card row only scrolls sideways, so the wheel does that.</summary>
    private void OnCardWheel(object sender, MouseWheelEventArgs e)
    {
        if (CardScroll.ScrollableWidth <= 0) return;
        e.Handled = true;
        CardScroll.ScrollToHorizontalOffset(CardScroll.HorizontalOffset - e.Delta);
    }

    /// <summary>The cells drive the frozen mod column (vertically) and
    /// the profile header (sideways).</summary>
    private void OnCellsScrolled(object sender, ScrollChangedEventArgs e)
    {
        NameScroll.ScrollToVerticalOffset(e.VerticalOffset);
        HeadScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
    }

    /// <summary>The wheel anywhere on the matrix scrolls the cells (Shift:
    /// sideways), 48 px a notch like a plain ScrollViewer.</summary>
    private void OnMatrixWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var step = e.Delta * 0.4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            CellScroll.ScrollToHorizontalOffset(CellScroll.HorizontalOffset - step);
        else
            CellScroll.ScrollToVerticalOffset(CellScroll.VerticalOffset - step);
    }
}
