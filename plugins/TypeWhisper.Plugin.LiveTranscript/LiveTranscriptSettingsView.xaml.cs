using System.Windows;
using System.Windows.Controls;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Settings view for the Live Transcript plugin.
/// Provides sliders for font size and window opacity.
/// </summary>
public partial class LiveTranscriptSettingsView : UserControl
{
    private readonly LiveTranscriptPlugin _plugin;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the LiveTranscriptSettingsView class.
    /// </summary>
    public LiveTranscriptSettingsView(LiveTranscriptPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        if (plugin.UsesHostAppearance)
        {
            FontSizePanel.Visibility = Visibility.Collapsed;
            AutoHidePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            FontSizeSlider.Value = plugin.FontSize;
            FontSizeLabel.Text = plugin.FontSize.ToString("0.#");

            AutoHideSlider.Value = plugin.AutoHideMilliseconds / 1000d;
            UpdateAutoHideLabel(AutoHideSlider.Value);
        }

        OpacitySlider.Value = plugin.Opacity;
        OpacityLabel.Text = $"{(int)(plugin.Opacity * 100)}%";

        _initialized = true;
    }

    private void FontSizeSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;

        var size = e.NewValue;
        FontSizeLabel.Text = size.ToString("0.#");
        _plugin.FontSize = size;
    }

    private void OpacitySlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;

        var opacity = e.NewValue;
        OpacityLabel.Text = $"{(int)(opacity * 100)}%";
        _plugin.Opacity = opacity;
    }

    private void AutoHideSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;

        UpdateAutoHideLabel(e.NewValue);
        _plugin.AutoHideMilliseconds = (int)Math.Round(e.NewValue * 1000, MidpointRounding.AwayFromZero);
    }

    private void ResetPositionButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _plugin.ResetWindowPosition();
    }

    private void UpdateAutoHideLabel(double seconds)
    {
        AutoHideLabel.Text = seconds <= 0
            ? "Off"
            : $"{seconds:0.##}s";
    }
}
