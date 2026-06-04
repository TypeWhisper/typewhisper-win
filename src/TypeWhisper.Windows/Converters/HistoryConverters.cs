using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Converters;

/// <summary>Truthy check: true for bool=true, non-empty strings, non-null objects.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
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

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// Provides inverse bool to visibility converter behavior.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// Provides expand collapse text converter behavior.
/// </summary>
public sealed class ExpandCollapseTextConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? $"{Loc.Instance["History.ShowLess"]} \u25B2" : $"{Loc.Instance["History.ShowMore"]} \u25BC";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
