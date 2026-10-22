using System.Windows;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

public partial class SetupWindow : Window
{
    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Done += Close;
    }
}
