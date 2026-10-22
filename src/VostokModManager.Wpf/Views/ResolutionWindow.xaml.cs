using System.Windows;
using System.Windows.Input;
#if AI_RESOLVER
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;
#endif

namespace VostokModManager.Wpf.Views;

/// <summary>AI edition: Claude's resolution of one file-overlap
/// conflict. In the Integrated edition the class exists (its XAML
/// compiles in both) but has no entry point.</summary>
public partial class ResolutionWindow : Window
{
    private ResolutionWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

#if AI_RESOLVER
    /// <summary>Opens over `owner`, runs <c>session.ResolveAsync(conflict)</c>
    /// with a working state, then shows the verdict. Completes when the
    /// window closes; true when a merge was applied to a mod.</summary>
    public static Task<bool> ShowAsync(Window owner, ManagerSession session, ConflictDetector.Conflict conflict)
    {
        var vm = new ResolutionViewModel(session, conflict);
        var w = new ResolutionWindow { DataContext = vm };
        w.Title = "Conflict resolution: " + conflict.Key;
        PromptOwner.Attach(w, owner);
        // The run starts once the window is up; ShowDialog keeps the UI
        // pumping while Claude works.
        w.Loaded += async (_, _) => await vm.RunAsync();
        using (PromptOwner.Use(session, w)) w.ShowDialog();
        return Task.FromResult(vm.Applied);
    }
#endif
}
