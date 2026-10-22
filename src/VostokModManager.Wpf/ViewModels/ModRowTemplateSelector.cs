using System.Windows;
using System.Windows.Controls;

namespace VostokModManager.Wpf.ViewModels;

/// <summary>Mods rows use the mod template; pack headers their own.</summary>
public sealed class ModRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ModTemplate { get; set; }
    public DataTemplate? PackTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is ModItem { IsPackHeader: true } ? PackTemplate : ModTemplate;
}
