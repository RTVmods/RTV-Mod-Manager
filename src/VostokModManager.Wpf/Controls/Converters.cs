using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Controls;

/// <summary>Looks a resource key up in the app's resources ("Icon.Mods" → its Geometry).</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Collapsed for null, false, 0, "" and empty lists; Visible otherwise.
/// With ConverterParameter "invert" the other way round.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.GetEnumerator().MoveNext(),
            _ => true,
        };
        if (parameter as string == "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True for a non-empty string (e.g. to enable a tooltip only when there is one).</summary>
public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Launch-panel check level → its dot colour.</summary>
public sealed class CheckLevelBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value switch
        {
            LaunchCheckLevel.Problem => "B.Salmon",
            LaunchCheckLevel.Notice => "B.Amber",
            _ => "B.Green",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Mods-list status → text colour.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value switch
        {
            RowStatusKind.Update => "B.AmberText",
            RowStatusKind.Conflict => "B.SalmonText",
            RowStatusKind.Testing => "B.Blue",
            RowStatusKind.Ambiguous => "B.Blue",
            RowStatusKind.UpToDate => "B.Text3",
            _ => "B.Text3",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Dims a disabled mod's row.</summary>
public sealed class EnabledOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.6;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"key" == ConverterParameter → true. For tab RadioButtons bound to a string.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
