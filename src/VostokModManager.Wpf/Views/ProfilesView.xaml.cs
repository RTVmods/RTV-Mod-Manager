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

    private ProfilesViewModel? Vm => DataContext as ProfilesViewModel;

    // ── the mod list: load order box ────────────────────────────────────

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

    // ── the mod list: copy menu ─────────────────────────────────────────

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
    private void OnModMenu(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm || vm.ModSelectedCount == 0)
        {
            e.Handled = true;
            return;
        }
        ModList.ContextMenu = BuildCopyMenu(vm, withRemove: true);
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
        var n = vm.ModSelectedCount;
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

    // ── the mod list → a profile on the left ────────────────────────────────────────

    private Point? _dragStart;
    /// <summary>A press on an already-selected row of a multi-selection:
    /// the selection is kept so the whole set can be dragged, and
    /// narrowed to that row on release if no drag started.</summary>
    private ListBoxItem? _pendingSelect;

    private void OnModMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pendingSelect = null;
        var source = e.OriginalSource as DependencyObject;
        // The row's own controls (tick, order box, Remove) work as usual.
        if (FindUp<TextBox>(source) != null || FindUp<ButtonBase>(source) != null) return;
        var container = FindUp<ListBoxItem>(source);
        if (container == null) return;
        _dragStart = e.GetPosition(ModList);
        if (container.IsSelected && ModList.SelectedItems.Count > 1
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _pendingSelect = container;
            e.Handled = true;
            ModList.Focus();
        }
    }

    private void OnModMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(ModList);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStart = null;
        _pendingSelect = null;
        if (Vm?.DragPayload() is not { } payload) return;
        var data = new DataObject(typeof(DraggedProfileMods), payload);
        DragDrop.DoDragDrop(ModList, data, DragDropEffects.Copy);
        foreach (var c in Vm?.Cards ?? Enumerable.Empty<ProfileCard>()) c.IsDropTarget = false;
    }

    private void OnModMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSelect is { } container)
        {
            ModList.SelectedItems.Clear();
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
        Dispatcher.BeginInvoke(() => _ = vm.CopyMods(card, payload));
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
}
