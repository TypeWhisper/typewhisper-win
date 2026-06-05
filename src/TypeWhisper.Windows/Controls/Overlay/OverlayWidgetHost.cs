using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Controls.Overlay;

/// <summary>
/// Provides overlay widget host behavior.
/// </summary>
public class OverlayWidgetHost : ContentControl
{
    /// <summary>
    /// Gets the widget type property.
    /// </summary>
    public static readonly DependencyProperty WidgetTypeProperty =
        DependencyProperty.Register(nameof(WidgetType), typeof(OverlayWidget), typeof(OverlayWidgetHost),
            new PropertyMetadata(OverlayWidget.None, OnWidgetTypeChanged));

    /// <summary>
    /// Gets the widget type.
    /// </summary>
    public OverlayWidget WidgetType
    {
        get => (OverlayWidget)GetValue(WidgetTypeProperty);
        set => SetValue(WidgetTypeProperty, value);
    }

    private static void OnWidgetTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is OverlayWidgetHost host)
            host.UpdateContent();
    }

    private void UpdateContent()
    {
        // Dispose previous widget if it implements cleanup
        if (Content is IDisposable disposable)
            disposable.Dispose();

        Content = WidgetType switch
        {
            OverlayWidget.Indicator => new IndicatorWidget(),
            OverlayWidget.Timer => new TimerWidget(),
            OverlayWidget.Waveform => new WaveformWidget(),
            OverlayWidget.Clock => new ClockWidget(),
            OverlayWidget.Profile => new ProfileWidget(),
            OverlayWidget.HotkeyMode => new HotkeyModeWidget(),
            OverlayWidget.AppName => new AppNameWidget(),
            _ => null
        };
    }
}
