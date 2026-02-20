using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.Converters;

/// <summary>
/// Converts audio level (0..1 float) + container width to a pixel width for the level bar.
/// </summary>
public sealed class AudioLevelWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;

        var level = values[0] is float f ? f : 0f;
        var maxWidth = values[1] is double d ? d : 40.0;

        // Clamp and scale (RMS values are typically 0..0.5 range)
        var normalized = Math.Min(level * 3f, 1f);
        return normalized * maxWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts an integer step value to Visibility. Shows Visible when step matches ConverterParameter, else Collapsed.
/// Usage: Visibility="{Binding CurrentStep, Converter={StaticResource StepConverter}, ConverterParameter=0}"
/// </summary>
public sealed class StepToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int step && parameter is string paramStr && int.TryParse(paramStr, out var target))
            return step == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Shows Visible when the string value is non-null and non-empty, else Collapsed.
/// Used for badge visibility in language dropdowns.
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a float level (0..1) to a width in pixels for a mic level bar.
/// The max width is passed as ConverterParameter (default 300).
/// </summary>
public sealed class LevelToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is float f ? f : 0f;
        var maxWidth = 300.0;
        if (parameter is string s && double.TryParse(s, CultureInfo.InvariantCulture, out var mw))
            maxWidth = mw;

        var normalized = Math.Min(level * 3f, 1f);
        return normalized * maxWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts seconds (double) to M:SS timer format.
/// </summary>
public sealed class SecondsToTimerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var seconds = value is double d ? d : 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts HotkeyMode? to a short label: TOG or PTT.
/// </summary>
public sealed class HotkeyModeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is HotkeyMode mode ? mode == HotkeyMode.Toggle ? "TOG" : "PTT" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Splits a comma-separated tags string into a string array for ItemsControl binding.
/// </summary>
public sealed class TagSplitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s)
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts (WordCount, ChartMaxValue) → proportional bar height in pixels.
/// ConverterParameter = max pixel height (default 160).
/// </summary>
public sealed class BarHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 4.0;
        int wordCount = values[0] is int wc ? wc : 0;
        int maxValue = values[1] is int mv ? mv : 1;
        double maxHeight = 160.0;
        if (parameter is string s && double.TryParse(s, CultureInfo.InvariantCulture, out var mh))
            maxHeight = mh;
        if (maxValue <= 0) return 4.0;
        return Math.Max(4.0, (double)wordCount / maxValue * maxHeight);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// DateTime → short day label like "7."
/// </summary>
public sealed class DayLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? $"{dt.Day}." : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a boolean value.
/// </summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// true → Collapsed, false → Visible (inverse of BooleanToVisibilityConverter).
/// </summary>
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// int 0 → Visible, non-zero → Collapsed.
/// Pass ConverterParameter="invert" to reverse.
/// </summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isZero = value is int i && i == 0;
        if (parameter is string s && s == "invert") isZero = !isZero;
        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
