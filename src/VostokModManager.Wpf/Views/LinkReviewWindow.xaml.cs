using System.Windows;
using System.Windows.Input;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Lists the mods not linked to VostokMods and links them.</summary>
public partial class LinkReviewWindow : Window
{
    public LinkReviewWindow(LinkReviewViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Opens the review over `owner`; the VostokMods list is read
    /// once the window is up.</summary>
    public static void Show(Window owner, ManagerSession session)
    {
        var vm = new LinkReviewViewModel(session);
        var w = new LinkReviewWindow(vm);
        PromptOwner.Attach(w, owner);
        w.Loaded += async (_, _) => await vm.LoadAsync();
        using (PromptOwner.Use(session, w)) w.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
