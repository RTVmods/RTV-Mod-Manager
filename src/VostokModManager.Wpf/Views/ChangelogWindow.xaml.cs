using System.Windows;
using VostokModManager.Wpf.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>A mod's changelog from VostokMods, every version newest first.</summary>
public partial class ChangelogWindow : Window
{
    public ChangelogWindow(string modName, string markdown)
    {
        InitializeComponent();
        Title = modName + " — changelog";
        ModName.Text = modName;
        Notes.Markdown = markdown;
    }

    public static void Show(Window? owner, string modName, string markdown)
    {
        var w = new ChangelogWindow(modName, markdown);
        PromptOwner.Attach(w, owner);
        w.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
