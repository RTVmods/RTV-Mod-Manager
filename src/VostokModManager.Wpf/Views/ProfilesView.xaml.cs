using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

    private ProfilesViewModel? Vm => DataContext as ProfilesViewModel;

    /// <summary>A click on a matrix row (not on its tick, ⋯ or a cell's
    /// own control) toggles its tick.</summary>
    private void OnCellRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatrixRowItem row }) row.IsChecked = !row.IsChecked;
    }

    // ── side panel: load order box ────────────────────────────────────

    private void OnPriorityKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProfileModItem item }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            item.CommitPriority();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.RevertPriority();
        }
    }

    private void OnPriorityLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ProfileModItem item }) item.CommitPriority();
    }

    // ── side panel: copy menu ─────────────────────────────────────────

    /// <summary>"Copy to profile…": a menu of the other profiles.</summary>
    private void OnCopyTo(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || Vm is not { } vm) return;
        var menu = BuildCopyMenu(vm, withRemove: false);
        menu.PlacementTarget = b;
        menu.Placement = PlacementMode.Bottom;
        b.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Right-click on the list: remove or copy the selection.</summary>
    private void OnDetailMenu(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm || vm.DetailSelectedCount == 0)
        {
            e.Handled = true;
            return;
        }
        DetailList.ContextMenu = BuildCopyMenu(vm, withRemove: true);
    }

    private static ContextMenu BuildCopyMenu(ProfilesViewModel vm, bool withRemove)
    {
        var menu = new ContextMenu();
        if (withRemove)
        {
            menu.Items.Add(new MenuItem { Header = vm.RemoveSelectedLabel + "…", Command = vm.RemoveSelectedModsCommand });
            menu.Items.Add(new Separator());
        }
        var targets = vm.CopyTargets;
        var n = vm.DetailSelectedCount;
        if (targets.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No other profile to copy into", IsEnabled = false });
            return menu;
        }
        menu.Items.Add(new MenuItem { Header = n == 1 ? "Copy it to" : $"Copy these {n} to", IsEnabled = false });
        foreach (var card in targets)
        {
            var target = card;
            menu.Items.Add(new MenuItem
            {
                Header = $"    {target.Name}",
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.CopySelectedTo(target)),
            });
        }
        return menu;
    }

    // ── side panel → card drag ────────────────────────────────────────

    private Point? _dragStart;
    /// <summary>A press on an already-selected row of a multi-selection:
    /// the selection is kept so the whole set can be dragged, and
    /// narrowed to that row on release if no drag started.</summary>
    private ListBoxItem? _pendingSelect;

    private void OnDetailMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pendingSelect = null;
        var source = e.OriginalSource as DependencyObject;
        // The row's own controls (tick, order box, Remove) work as usual.
        if (FindUp<TextBox>(source) != null || FindUp<ButtonBase>(source) != null) return;
        var container = FindUp<ListBoxItem>(source);
        if (container == null) return;
        _dragStart = e.GetPosition(DetailList);
        if (container.IsSelected && DetailList.SelectedItems.Count > 1
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _pendingSelect = container;
            e.Handled = true;
            DetailList.Focus();
        }
    }

    private void OnDetailMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(DetailList);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStart = null;
        _pendingSelect = null;
        if (Vm?.DragPayload() is not { } payload) return;
        var data = new DataObject(typeof(DraggedProfileMods), payload);
        DragDrop.DoDragDrop(DetailList, data, DragDropEffects.Copy);
        foreach (var c in Vm?.Cards ?? Enumerable.Empty<ProfileCard>()) c.IsDropTarget = false;
    }

    private void OnDetailMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSelect is { } container)
        {
            DetailList.SelectedItems.Clear();
            container.IsSelected = true;
        }
        _pendingSelect = null;
        _dragStart = null;
    }

    private static DraggedProfileMods? Payload(DragEventArgs e)
        => e.Data.GetDataPresent(typeof(DraggedProfileMods))
            ? e.Data.GetData(typeof(DraggedProfileMods)) as DraggedProfileMods
            : null;

    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ProfileCard card } && Payload(e) is { } payload
            && ProfilesViewModel.CanDrop(card, payload))
        {
            e.Effects = DragDropEffects.Copy;
            card.IsDropTarget = true;
        }
        else e.Effects = DragDropEffects.None;
    }

    private void OnCardDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileCard card }) card.IsDropTarget = false;
    }

    private void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: ProfileCard card } || Payload(e) is not { } payload) return;
        card.IsDropTarget = false;
        if (!ProfilesViewModel.CanDrop(card, payload) || Vm is not { } vm) return;
        // After the drag loop has unwound: the copy's message box and
        // the reload (which rebuilds this card) shouldn't run inside it.
        Dispatcher.BeginInvoke(() => vm.CopyMods(card, payload));
    }

    private static T? FindUp<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
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
