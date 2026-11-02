using System.Windows;
using System.Windows.Input;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>Shows exactly how the live mods differ from the active
/// profile before "save live state into the profile" writes anything.</summary>
public partial class DriftReviewWindow : Window
{
    private DriftChoice _choice = DriftChoice.Cancel;

    public DriftReviewWindow(IReadOnlyList<DriftItem> items, string profileName)
    {
        InitializeComponent();
        Title = $"Drift — live state vs '{profileName}'";
        TitleText.Text = $"The live mods drift from '{profileName}'";
        var missing = items.Count(i => i.NotLive);
        Intro.Text = (items.Count == 1 ? "1 change" : $"{items.Count} changes") + " below. "
            + $"Save to profile writes the mods folder as it is (on/off, load order, version) into '{profileName}'"
            + (missing > 0 ? $", and drops the {missing} mod{(missing == 1 ? "" : "s")} the folder doesn't have." : ".")
            + (missing > 0 ? $" Install does the opposite for those: it puts them in the mods folder, from My Mods or VostokMods." : "")
            + " Cancel leaves both as they are.";
        if (missing > 0)
            InstallButton.Content = missing == 1 ? "Install the missing mod" : $"Install the {missing} missing";
        else
        {
            // Nothing to install: saving is the one action.
            InstallButton.Visibility = Visibility.Collapsed;
            SaveButton.Style = (Style)FindResource("Btn.Primary");
            SaveButton.IsDefault = true;
            SaveButton.Margin = new Thickness(0);
        }
        Rows.ItemsSource = items;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Shows the drift over `owner`: Save (the profile takes the
    /// live state), Install (the folder gets the profile's missing mods)
    /// or Cancel.</summary>
    public static DriftChoice Review(Window? owner, IReadOnlyList<DriftItem> items, string profileName)
    {
        var w = new DriftReviewWindow(items, profileName);
        PromptOwner.Attach(w, owner);
        w.ShowDialog();
        return w._choice;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _choice = DriftChoice.Save;
        Close();
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        _choice = DriftChoice.Install;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>What the drift review asked for.</summary>
public enum DriftChoice { Cancel, Save, Install }
