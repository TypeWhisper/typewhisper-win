using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TypeWhisper.Windows.Converters;

/// <summary>Truthy check: true for bool=true, non-empty strings, non-null objects.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            null => Visibility.Collapsed,
            bool b => b ? Visibility.Visible : Visibility.Collapsed,
            string s => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible,
            _ => Visibility.Visible
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

public sealed class ExpandCollapseTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Weniger \u25B2" : "Mehr anzeigen \u25BC";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
