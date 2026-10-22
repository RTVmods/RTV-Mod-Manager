using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Every detected conflict by severity tier. Double-click (or
/// Details) opens the conflict's detail; in the AI edition a file overlap
/// can be sent to Claude. Refreshes itself whenever the session changes.</summary>
public partial class ConflictsWindow : Window
{
    private readonly ManagerSession _session;
    private readonly ConflictsViewModel _vm;

    public ConflictsWindow(ManagerSession session, string? focusModId = null)
    {
        InitializeComponent();
        _session = session;
        DataContext = _vm = new ConflictsViewModel(session, focusModId);
        _vm.OpenDetails = row => ConflictDetailWindow.Show(this, _session, row.Conflict);
        _vm.RunResolve = ResolveAsync;
        Loaded += (_, _) =>
        {
            if (_vm.FirstFocused is { } first)
            {
                _vm.Selected = first;
                List.ScrollIntoView(first);
            }
        };
        Closed += (_, _) => _vm.Dispose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Opens the conflicts list over `owner`. With `focusModId`,
    /// that mod's conflicts are marked and listed first in their tier.</summary>
    public static void Show(Window owner, ManagerSession session, string? focusModId = null)
    {
        var w = new ConflictsWindow(session, focusModId);
        PromptOwner.Attach(w, owner);
        using (PromptOwner.Use(session, w)) w.ShowDialog();
    }

    private async Task ResolveAsync(ConflictRowItem row)
    {
#if AI_RESOLVER
        await ResolutionWindow.ShowAsync(this, _session, row.Conflict);
        // An applied merge rescans (the session's Changed refreshes the
        // list); refresh anyway so the Resolve buttons re-enable.
        _vm.Refresh();
#else
        await Task.CompletedTask;
#endif
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Not when the double click landed on a row's button.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != List; d = ParentOf(d))
        {
            if (d is ButtonBase) return;
            if (d is ListBoxItem { DataContext: ConflictRowItem row })
            {
                _vm.DetailsCommand.Execute(row);
                e.Handled = true;
                return;
            }
        }
    }

    private static DependencyObject? ParentOf(DependencyObject d)
        => d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.Selected != null)
        {
            _vm.DetailsCommand.Execute(_vm.Selected);
            e.Handled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
