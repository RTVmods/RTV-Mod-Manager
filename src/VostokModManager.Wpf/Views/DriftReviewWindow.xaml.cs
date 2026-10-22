using System.Windows;
using System.Windows.Input;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>Shows exactly how the live mods differ from the active
/// profile before "save live state into the profile" writes anything.</summary>
public partial class DriftReviewWindow : Window
{
    private bool _save;

    public DriftReviewWindow(IReadOnlyList<DriftItem> items, string profileName)
    {
        InitializeComponent();
        Title = $"Drift — live state vs '{profileName}'";
        TitleText.Text = $"The live mods drift from '{profileName}'";
        Intro.Text = (items.Count == 1 ? "1 change" : $"{items.Count} changes") + " below. "
            + $"Save to profile writes the current live state (enabled, priority, version) into '{profileName}'. "
            + "Cancel leaves both as they are.";
        Rows.ItemsSource = items;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>The `review` callback for
    /// <c>session.SyncLiveStateIntoActiveProfile(review)</c>: true when the
    /// user chose to save the live state into `profileName`.</summary>
    public static bool Review(Window? owner, IReadOnlyList<DriftItem> items, string profileName)
    {
        var w = new DriftReviewWindow(items, profileName);
        PromptOwner.Attach(w, owner);
        w.ShowDialog();
        return w._save;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _save = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
