using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TypeWhisper.Plugin.LiveTranscript;

/// <summary>
/// Floating transparent window that displays live transcription text.
/// Positioned at the bottom center of the primary screen.
/// </summary>
public partial class LiveTranscriptWindow : Window
{
    private const double FallbackHeight = 80;
    private const double DefaultBottomOffset = 88;
    private double? _savedLeft;
    private double? _savedTop;

    /// <summary>
    /// Initializes a new instance of the LiveTranscriptWindow class.
    /// </summary>
    public LiveTranscriptWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when position changes.
    /// </summary>
    public event Action<double, double>? PositionChanged;

    /// <summary>Gets the current displayed text.</summary>
    public string CurrentText => TranscriptText.Text;

    /// <summary>Shows the transcript with a short, restrained entrance motion.</summary>
    public void ShowAnimated()
    {
        if (!IsVisible)
            Show();

        if (!SystemParameters.ClientAreaAnimation)
            return;

        var targetOpacity = RootBorder.Opacity;
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        RootBorder.BeginAnimation(OpacityProperty, null);
        RootBorder.Opacity = targetOpacity;
        EntranceScale.ScaleX = 1;
        EntranceScale.ScaleY = 1;
        EntranceTranslate.Y = 0;

        RootBorder.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, targetOpacity, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        EntranceScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        EntranceScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        EntranceTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
    }

    /// <summary>Updates the displayed transcript text and scrolls to the bottom.</summary>
    public void UpdateText(string text)
    {
        TranscriptText.Text = text;
        TranscriptScroller.ScrollToEnd();
    }

    /// <summary>Sets the font size of the transcript text.</summary>
    public void SetFontSize(double size)
    {
        TranscriptText.FontSize = size;
    }

    /// <summary>Sets the background opacity of the window border.</summary>
    public void SetWindowOpacity(double opacity)
    {
        RootBorder.Opacity = opacity;
    }

    /// <summary>
    /// Sets saved position.
    /// </summary>
    public void SetSavedPosition(double? left, double? top)
    {
        _savedLeft = left;
        _savedTop = top;

        if (IsLoaded)
            PositionWindow();
    }

    /// <summary>
    /// Performs reset to default position.
    /// </summary>
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
        Top = workArea.Bottom - height - DefaultBottomOffset;
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

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
