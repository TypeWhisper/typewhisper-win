using System.Windows;
using System.Windows.Input;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Floating transparent window that displays live transcription text.
/// Positioned at the bottom center of the primary screen.
/// </summary>
public partial class LiveTranscriptWindow : Window
{
    private const double FallbackHeight = 80;
    private double? _savedLeft;
    private double? _savedTop;

    public LiveTranscriptWindow()
    {
        InitializeComponent();
    }

    public event Action<double, double>? PositionChanged;

    /// <summary>Gets the current displayed text.</summary>
    public string CurrentText => TranscriptText.Text;

    /// <summary>Updates the displayed transcript text and scrolls to the bottom.</summary>
    public void UpdateText(string text)
    {
        TranscriptText.Text = text;
        TranscriptScroller.ScrollToEnd();
    }

    /// <summary>Sets the font size of the transcript text.</summary>
    public void SetFontSize(int size)
    {
        TranscriptText.FontSize = size;
    }

    /// <summary>Sets the background opacity of the window border.</summary>
    public void SetWindowOpacity(double opacity)
    {
        RootBorder.Opacity = opacity;
    }

    public void SetSavedPosition(double? left, double? top)
    {
        _savedLeft = left;
        _savedTop = top;

        if (IsLoaded)
            PositionWindow();
    }

    public void ResetToDefaultPosition()
    {
        _savedLeft = null;
        _savedTop = null;

        if (IsLoaded)
            PositionAtBottomCenter();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
    }

    private void PositionWindow()
    {
        if (_savedLeft is { } left && _savedTop is { } top)
        {
            PositionWithinWorkArea(left, top);
            return;
        }

        PositionAtBottomCenter();
    }

    private void PositionAtBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        var width = GetEffectiveWidth();
        var height = GetEffectiveHeight();
        Left = workArea.Left + (workArea.Width - width) / 2;
        Top = workArea.Bottom - height - 40;
    }

    private void PositionWithinWorkArea(double left, double top)
    {
        var workArea = SystemParameters.WorkArea;
        var width = GetEffectiveWidth();
        var height = GetEffectiveHeight();

        Left = Clamp(left, workArea.Left, workArea.Right - width);
        Top = Clamp(top, workArea.Top, workArea.Bottom - height);
    }

    private double GetEffectiveWidth() =>
        ActualWidth > 0 ? ActualWidth : Width;

    private double GetEffectiveHeight() =>
        ActualHeight > 0
            ? ActualHeight
            : !double.IsNaN(Height) && Height > 0
                ? Height
                : FallbackHeight;

    private static double Clamp(double value, double min, double max) =>
        Math.Clamp(value, min, Math.Max(min, max));

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
            _savedLeft = Left;
            _savedTop = Top;
            PositionChanged?.Invoke(Left, Top);
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse state changes before WPF starts dragging.
        }
    }

    /// <summary>
    /// Re-position when size changes (SizeToContent="Height" may change actual height).
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsLoaded)
            PositionWindow();
    }
}
