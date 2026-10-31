using System.Windows;
using System.Windows.Input;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Where a mod list or modpack goes, with what the active profile
/// is missing from it. `Choice`: 0 the active profile, 1 a new profile,
/// 2 another saved profile, -1 cancelled.</summary>
public partial class ImportTargetWindow : Window
{
    public ImportTargetWindow(ImportTargetViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    public int Choice { get; private set; } = -1;

    private void Pick(int choice)
    {
        Choice = choice;
        Close();
    }

    private void OnAdd(object sender, RoutedEventArgs e) => Pick(0);
    private void OnNew(object sender, RoutedEventArgs e) => Pick(1);
    private void OnOther(object sender, RoutedEventArgs e) => Pick(2);
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
