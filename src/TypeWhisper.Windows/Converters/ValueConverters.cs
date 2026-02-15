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
