using System.Windows;
using System.Windows.Input;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Picks library mods to add to the active profile.</summary>
public partial class LibraryPickerWindow : Window
{
    public LibraryPickerWindow(LibraryPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Done += Close;
        Loaded += (_, _) => FilterBox.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Opens the picker over `owner`. The ticked library files,
    /// or none when the user cancelled.</summary>
    public static IReadOnlyList<string> Pick(Window owner, ManagerSession session)
    {
        var vm = new LibraryPickerViewModel(session);
        var w = new LibraryPickerWindow(vm);
        PromptOwner.Attach(w, owner);
        using (PromptOwner.Use(session, w)) w.ShowDialog();
        return vm.Picked;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
