using System.Windows;
using VostokModManager.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>The active profile's dependency rollup: every mod that
/// declares dependencies, each with its live state. Read-only.</summary>
public partial class ProfileDependenciesWindow : Window
{
    public ProfileDependenciesWindow(ProfileDependencyReport report)
    {
        InitializeComponent();
        DataContext = report;
        Title = TitleText.Text = report.ProfileName == null
            ? "Dependencies"
            : $"Dependencies in '{report.ProfileName}'";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
