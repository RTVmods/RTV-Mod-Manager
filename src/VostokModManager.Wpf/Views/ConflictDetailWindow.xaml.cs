using System.Windows;
using System.Windows.Input;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>One conflict in detail: hook collisions per method with the
/// last play session's evidence, every other type as mod → value.</summary>
public partial class ConflictDetailWindow : Window
{
    public ConflictDetailWindow(ConflictDetailViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Opens the detail of `conflict` over `owner`.</summary>
    public static void Show(Window? owner, ManagerSession session, ConflictDetector.Conflict conflict)
    {
        var vm = new ConflictDetailViewModel(session.GetConflictDetail(conflict), conflict);
        var w = new ConflictDetailWindow(vm);
        PromptOwner.Attach(w, owner);
        w.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
