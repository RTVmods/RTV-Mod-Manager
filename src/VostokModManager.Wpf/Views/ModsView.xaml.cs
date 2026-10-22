using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class ModsView : UserControl
{
    private const string RowDragFormat = "VostokModManager.ModRows";
    private Point _dragStart;
    private ModItem? _pressed;

    public ModsView() => InitializeComponent();

    private ModsViewModel? Vm => DataContext as ModsViewModel;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        Vm.MultiSelection.Clear();
        Vm.MultiSelection.AddRange(List.SelectedItems.OfType<ModItem>());
    }

    private void OnNoteLostFocus(object sender, RoutedEventArgs e) => Vm?.SaveNote(NoteBox.Text);

    /// <summary>A button whose menu opens on a left click (Install mod ▾, Switch ▾).</summary>
    private static void OpenMenu(object sender)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = b.DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnInstallMenu(object sender, RoutedEventArgs e) => OpenMenu(sender);
    private void OnSwitchMenu(object sender, RoutedEventArgs e) => OpenMenu(sender);

    /// <summary>Esc in the filter box clears it.</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm == null) return;
        Vm.Query = "";
        e.Handled = true;
    }

    /// <summary>Enter in a load-order box commits it (and Esc puts the old value back).</summary>
    private void OnOrdKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ModItem item } box) return;
        if (e.Key == Key.Enter)
        {
            item.CommitPriority();
            List.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item.PriorityText = item.Priority.ToString();
            List.Focus();
            e.Handled = true;
        }
    }

    private void OnOrdLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ModItem item }) item.CommitPriority();
    }

    /// <summary>Double-click a mod opens its VostokMods page (not on the
    /// tick, the load-order box or the status button).</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindParent<CheckBox>(src) != null || FindParent<TextBox>(src) != null || FindParent<Button>(src) != null) return;
        if (ItemAt(src) is { Entry: not null } item) Vm?.OpenItemPageCommand.Execute(item);
    }

    /// <summary>Delete removes the selected mods; F5 refreshes.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || e.OriginalSource is TextBox) return;
        if (e.Key == Key.Delete && Vm.Selected != null)
        {
            Vm.DeleteCommand.Execute(Vm.Selected);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Vm.RefreshAllCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── drag rows to reorder ──────────────────────────────────────────

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(List);
        _pressed = ItemAt(e.OriginalSource as DependencyObject);
        // Clicks on the checkbox are toggles, not the start of a drag.
        if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null) _pressed = null;
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed?.Entry == null) return;
        var d = e.GetPosition(List) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var moved = List.SelectedItems.OfType<ModItem>().Contains(_pressed)
            ? List.SelectedItems.OfType<ModItem>().Where(m => m.Entry != null).ToList()
            : new List<ModItem> { _pressed };
        _pressed = null;
        DragDrop.DoDragDrop(List, new DataObject(RowDragFormat, moved), DragDropEffects.Move);
    }

    // ── drop: rows on a row reorder, files anywhere install ──────────

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(RowDragFormat) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (Vm == null) return;
        if (e.Data.GetData(RowDragFormat) is List<ModItem> moved)
        {
            var target = ItemAt(e.OriginalSource as DependencyObject);
            if (target != null) Vm.Reorder(moved, target);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await Vm.DropAsync(files);
    }

    private ModItem? ItemAt(DependencyObject? source)
        => FindParent<ListBoxItem>(source)?.DataContext as ModItem;

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
