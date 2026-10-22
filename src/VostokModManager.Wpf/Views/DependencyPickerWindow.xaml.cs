using System.Windows;
using System.Windows.Input;
using VostokModManager.Domain;
using VostokModManager.Services;
using VostokModManager.Wpf.Services;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>Edits a mod's required dependencies: tick installed mods (so
/// an id can't be mistyped) or add one that isn't installed yet.</summary>
public partial class DependencyPickerWindow : Window
{
    private readonly DependencyPickerViewModel _vm;

    public DependencyPickerWindow(DependencyPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = "Required dependencies — " + vm.ModLabel;
        Loaded += (_, _) => FilterBox.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Opens the picker for `mod` over `owner`. True when the
    /// mod's mod.txt was rewritten with a new required list.</summary>
    public static Task<bool> EditAsync(Window owner, ManagerSession session, ModEntry mod)
    {
        var vm = new DependencyPickerViewModel(session, mod);
        var w = new DependencyPickerWindow(vm);
        PromptOwner.Attach(w, owner);
        using (PromptOwner.Use(session, w)) w.ShowDialog();
        return Task.FromResult(vm.Saved);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.SaveAsync();
        }
        catch (Exception ex)
        {
            MessageWindow.Create(this, "Couldn't save the dependencies", ex.Message, PromptKind.Error, new[] { "OK" }, 0, 0).ShowDialog();
            return;
        }
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_vm.AddCommand.CanExecute(null)) _vm.AddCommand.Execute(null);
        e.Handled = true;
    }
}
