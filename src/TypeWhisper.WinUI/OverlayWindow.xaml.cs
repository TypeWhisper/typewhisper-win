using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace TypeWhisper.WinUI;

internal enum PrototypeOverlayMode { Standard, Compact, Minimal }

public sealed partial class OverlayWindow : Window
{
    internal const int WindowWidth = 458;
    internal const int WindowHeight = 72;
    private const int GwlExstyle = -20;
    private const long WsExNoactivate = 0x08000000L;
    private const long WsExToolwindow = 0x00000080L;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private readonly Stopwatch _duration = new();
    private readonly float[] _levels = new float[64];
    private readonly PrototypeAudioLevelSource _audioLevelSource = new();
    private readonly Random _random = new(73);
    private TranscriptPreviewWindow? _transcriptWindow;
    private long _lastCompositionTimestamp;
    private double _renderAccumulatorTicks;
    private double _phase;
    private bool _useMicrophone;
    private bool _isLoaded;
    private bool _transcriptPreviewEnabled;
    private PrototypeOverlayMode _mode;
    private bool _previewVisible;
    private bool _sessionStarted;
    private bool _paused;
    private bool _technicalDetails;
    private int _diagnosticDrawCount;
    private long _diagnosticSampleStart = Stopwatch.GetTimestamp();
    private double _scale = 1;
    private int _logicalWidth = WindowWidth;
    private int _logicalHeight = WindowHeight;
    private PrototypeOverlayPreferences _layout = new(PrototypeOverlayMode.Standard, true, false);
    internal void SetLayout(PrototypeOverlayPreferences preferences) => _layout = preferences;

    internal bool IsPaused => _paused;
    internal bool IsPreviewVisible => _previewVisible;

    internal OverlayWindow(bool transcriptPreviewEnabled = true)
    {
        _transcriptPreviewEnabled = transcriptPreviewEnabled;
        InitializeComponent();
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        NativeWindowAppearance.RemoveOverlayFrame(this);
        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        PositionBottomCenter();

        OverlayRoot.Loaded += (_, _) =>
        {
            NativeWindowAppearance.RemoveOverlayFrame(this);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => NativeWindowAppearance.RemoveOverlayFrame(this));
            _isLoaded = true;
            if (_previewVisible)
                BeginPreview();
            WaveformCanvas.Invalidate();
        };

        Closed += (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _audioLevelSource.Dispose();
            WaveformCanvas.RemoveFromVisualTree();
            _transcriptWindow?.Close();
            _transcriptWindow = null;
            SystemBackdrop = null;
        };
    }

    internal void ActivateWithoutTakingFocus()
    {
        var starting = !_previewVisible;
        _previewVisible = true;
        ConfigureNativeWindow();
        AppWindow.Show(activateWindow: false);
        NativeWindowAppearance.RemoveOverlayFrame(this);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => NativeWindowAppearance.RemoveOverlayFrame(this));
        if (starting && _isLoaded)
            BeginPreview();
    }

    private void BeginPreview()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        _paused = false;
        _useMicrophone = _audioLevelSource.TryStart();
        _duration.Restart();
        Array.Clear(_levels);
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CompositionTarget_Rendering;
        UpdateStateAppearance();
        FadeIn();
        SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
    }

    internal void HidePreview()
    {
        _previewVisible = false;
        _sessionStarted = false;
        _paused = false;
        _duration.Stop();
        _audioLevelSource.Dispose();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _transcriptWindow?.HideImmediately();
        AppWindow.Hide();
    }

    internal void TogglePaused()
    {
        if (!_previewVisible) return;
        _paused = !_paused;
        if (_paused)
        {
            _duration.Stop();
            _audioLevelSource.Dispose();
            Array.Clear(_levels);
        }
        else
        {
            _duration.Start();
            _useMicrophone = _audioLevelSource.TryStart();
        }
        _transcriptWindow?.SetPaused(_paused);
        UpdateStateAppearance();
        WaveformCanvas.Invalidate();
    }

    private void UpdateStateAppearance()
    {
        StatusText.Text = _paused ? "PAUSED" : "RECORDING";
        StatusText.Foreground = new SolidColorBrush(_paused
            ? Color.FromArgb(255, 244, 188, 106) : Color.FromArgb(255, 59, 167, 255));
        RecordingDot.Visibility = _paused ? Visibility.Collapsed : Visibility.Visible;
        if (_mode != PrototypeOverlayMode.Minimal)
        {
            UpdateWidgetText(LeftWidgetText, _layout.Left);
            UpdateWidgetText(RightWidgetText, _layout.Right);
        }
        PauseMark.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(OverlayRoot, $"{_mode} recording preview · {(_paused ? "Paused" : "Recording")}");
        DiagnosticsText.Text = _paused ? "Paused" : "Measuring…";
        _diagnosticSampleStart = Stopwatch.GetTimestamp();
        _diagnosticDrawCount = 0;
    }

    internal void SetMode(PrototypeOverlayMode mode, DisplayArea area)
    {
        _mode = mode;
        var minimal = mode == PrototypeOverlayMode.Minimal;
        var compact = mode == PrototypeOverlayMode.Compact;
        _logicalWidth = minimal ? 112 : compact ? 280 : WindowWidth;
        _logicalHeight = minimal ? 22 : compact ? 36 : WindowHeight;
        OverlayRoot.Padding = minimal ? new Thickness(16, 4, 16, 4) : compact ? new Thickness(12, 6, 12, 6) : new Thickness(14, 10, 14, 10);
        DotHost.Visibility = minimal || compact ? Visibility.Collapsed : Visibility.Visible;
        DotHost.Width = DotHost.Height = compact ? 28 : 34;
        StatusHost.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        StatusHost.Width = compact ? 42 : 104;
        StatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticsText.Visibility = _technicalDetails && mode == PrototypeOverlayMode.Standard ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Margin = compact ? new Thickness(0) : new Thickness(0, 2, 0, 0);
        StatusText.FontSize = compact ? 8 : 9;
        WaveformHost.Height = minimal ? 14 : compact ? 24 : 42;
        RecordingLayout.ColumnSpacing = minimal ? 0 : 12;
        ApplyWidgets();

        var work = area.WorkArea;
        var currentArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (currentArea?.DisplayId != area.DisplayId)
            AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _scale = dpi == 0 ? 1 : dpi / 96d;
        var width = Math.Min((int)Math.Round(_logicalWidth * _scale), Math.Max(1, work.Width - (int)(32 * _scale)));
        var height = (int)Math.Round(_logicalHeight * _scale);
        var edgeMargin = minimal ? 0 : (int)Math.Round(38 * _scale);
        var sideMargin = (int)Math.Round(24 * _scale);
        var x = _layout.HorizontalIndex switch { 0 => work.X + sideMargin, 2 => work.X + work.Width - width - sideMargin, _ => work.X + (work.Width - width) / 2 };
        AppWindow.MoveAndResize(new RectInt32(x,
            _layout.AtTop ? work.Y + edgeMargin : work.Y + work.Height - height - edgeMargin, width, height));
        NativeWindowAppearance.RemoveOverlayFrame(this);

        if (minimal)
        {
            _transcriptWindow?.HideImmediately();
            SetJoinedShape(false);
        }
        else
        {
            AnchorTranscript();
            SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
            if (!_transcriptPreviewEnabled) SetJoinedShape(false);
        }
        UpdateStateAppearance();
        WaveformCanvas.Invalidate();
    }

    internal void SetTranscriptPreviewEnabled(bool enabled)
    {
        _transcriptPreviewEnabled = enabled;
        if (!_isLoaded || !_previewVisible || _mode == PrototypeOverlayMode.Minimal)
            return;

        if (enabled)
            ShowTranscriptPreview();
        else
            _transcriptWindow?.SetExpanded(false, AppWindow.Position);
    }

    internal void SetTechnicalDetailsEnabled(bool enabled)
    {
        _technicalDetails = enabled;
        DiagnosticsText.Visibility = enabled && _mode == PrototypeOverlayMode.Standard ? Visibility.Visible : Visibility.Collapsed;
        _diagnosticSampleStart = Stopwatch.GetTimestamp();
        _diagnosticDrawCount = 0;
        DiagnosticsText.Text = _paused ? "Paused" : "Measuring…";
    }

    private void ShowTranscriptPreview()
    {
        SetJoinedShape(true);
        if (_transcriptWindow is null)
        {
            _transcriptWindow = new TranscriptPreviewWindow();
            _transcriptWindow.Collapsed += (_, _) => SetJoinedShape(false);
            _transcriptWindow.Closed += (_, _) =>
            {
                _transcriptWindow = null;
                SetJoinedShape(false);
            };
            AnchorTranscript();
            _transcriptWindow.ShowWithoutTakingFocus(AppWindow.Position);
        }

        AnchorTranscript();
        _transcriptWindow.SetPaused(_paused);
        _transcriptWindow.SetExpanded(true, AppWindow.Position);
    }

    private void SetJoinedShape(bool joined)
    {
        OverlayRoot.CornerRadius = _mode == PrototypeOverlayMode.Minimal
            ? (_layout.AtTop ? new CornerRadius(0, 0, 9, 9) : new CornerRadius(9, 9, 0, 0)) : joined
            ? (_layout.AtTop ? new CornerRadius(14, 14, 0, 0) : new CornerRadius(0, 0, 14, 14))
            : new CornerRadius(14);
    }

    private void AnchorTranscript() => _transcriptWindow?.SetAnchor(AppWindow.Position, AppWindow.Size.Width, _scale, _layout.AtTop, AppWindow.Size.Height);

    private void ApplyWidgets()
    {
        var minimal = _mode == PrototypeOverlayMode.Minimal;
        var left = minimal ? PrototypeOverlayWidget.Waveform : _layout.Left;
        var right = minimal ? PrototypeOverlayWidget.None : _layout.Right;
        WaveformHost.Visibility = left == PrototypeOverlayWidget.Waveform || right == PrototypeOverlayWidget.Waveform ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(WaveformHost, right == PrototypeOverlayWidget.Waveform ? 2 : 1);
        StatusHost.Visibility = left == PrototypeOverlayWidget.Timer || right == PrototypeOverlayWidget.Timer ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(StatusHost, left == PrototypeOverlayWidget.Timer ? 1 : 2);
        StatusHost.Width = _mode == PrototypeOverlayMode.Compact ? 42 : 104;
        RecordingLayout.ColumnDefinitions[1].Width = left == PrototypeOverlayWidget.None ? GridLength.Auto : right == PrototypeOverlayWidget.Waveform ? new GridLength(1, GridUnitType.Auto) : new GridLength(1, GridUnitType.Star);
        RecordingLayout.ColumnDefinitions[2].Width = right == PrototypeOverlayWidget.Waveform ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        RightWidgetText.MaxWidth = _mode == PrototypeOverlayMode.Compact ? 100 : 150;
        UpdateWidgetText(LeftWidgetText, left);
        UpdateWidgetText(RightWidgetText, right);
        // A mandatory status dot remains even when both configurable slots are empty.
        DotHost.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        DotHost.Width = DotHost.Height = _mode == PrototypeOverlayMode.Compact ? 18 : 34;
    }

    private void UpdateWidgetText(TextBlock text, PrototypeOverlayWidget widget)
    {
        text.Visibility = widget is PrototypeOverlayWidget.None or PrototypeOverlayWidget.Timer or PrototypeOverlayWidget.Waveform ? Visibility.Collapsed : Visibility.Visible;
        text.Text = widget switch
        {
            PrototypeOverlayWidget.Clock => DateTime.Now.ToString("HH:mm"),
            PrototypeOverlayWidget.Profile => "Default profile",
            PrototypeOverlayWidget.HotkeyMode => "Toggle",
            PrototypeOverlayWidget.AppName => "Quick Launch",
            PrototypeOverlayWidget.Indicator => _paused ? "Paused" : "Recording",
            _ => ""
        };
    }

    private void PositionBottomCenter()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null)
            return;

        var work = area.WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - WindowWidth) / 2,
            work.Y + work.Height - WindowHeight - 38));
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        if (_paused || !_previewVisible) return;
        var timestamp = Stopwatch.GetTimestamp();
        var targetTicks = Stopwatch.Frequency / 60d;
        if (_lastCompositionTimestamp == 0)
            _lastCompositionTimestamp = timestamp - (long)targetTicks;

        var elapsedTicks = Math.Min(timestamp - _lastCompositionTimestamp, targetTicks * 4);
        _lastCompositionTimestamp = timestamp;
        _renderAccumulatorTicks += elapsedTicks;
        if (_renderAccumulatorTicks < targetTicks)
            return;
        _renderAccumulatorTicks %= targetTicks;

        _phase += 0.19;
        var carrier = (Math.Sin(_phase) + 1) * 0.5;
        var phrase = (Math.Sin(_phase * 0.16) + 1) * 0.5;
        var demoRms = Math.Clamp((carrier * 0.56 + _random.NextDouble() * 0.16) * phrase, 0.025, 0.94);
        var dbfs = _useMicrophone ? _audioLevelSource.LatestDbfs : 20 * Math.Log10(demoRms);
        var sampleSeconds = (timestamp - _diagnosticSampleStart) / (double)Stopwatch.Frequency;
        if (sampleSeconds >= 1)
        {
            if (_technicalDetails && _mode == PrototypeOverlayMode.Standard)
                DiagnosticsText.Text = $"{dbfs:0} dBFS · {_diagnosticDrawCount / sampleSeconds:0} fps";
            _diagnosticSampleStart = timestamp;
            _diagnosticDrawCount = 0;
        }
        var normalized = (float)Math.Clamp((dbfs + 60) / 60, 0, 1);

        Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
        var previous = _levels[^2];
        _levels[^1] = normalized > previous
            ? previous + (normalized - previous) * 0.68f
            : previous + (normalized - previous) * 0.20f;

        DurationText.Text = _duration.Elapsed.ToString(@"mm\:ss");
        if (_mode != PrototypeOverlayMode.Minimal)
        {
            UpdateWidgetText(LeftWidgetText, _layout.Left);
            UpdateWidgetText(RightWidgetText, _layout.Right);
        }
        WaveformCanvas.Invalidate();
    }

    private void WaveformCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _diagnosticDrawCount++;
        var size = sender.Size;
        if (size.Width <= 1 || size.Height <= 1)
            return;

        if (_paused && _mode == PrototypeOverlayMode.Minimal)
        {
            var pauseColor = Color.FromArgb(255, 244, 188, 106);
            args.DrawingSession.FillRoundedRectangle((float)size.Width / 2 - 5, 2, 3, 10, 1, 1, pauseColor);
            args.DrawingSession.FillRoundedRectangle((float)size.Width / 2 + 2, 2, 3, 10, 1, 1, pauseColor);
            return;
        }
        var barCount = _mode == PrototypeOverlayMode.Minimal ? 16 : _mode == PrototypeOverlayMode.Compact ? 28 : 44;
        var center = (float)size.Height / 2;
        var width = (float)size.Width;
        var slotWidth = width / barCount;
        var barWidth = MathF.Max(2, slotWidth * 0.54f);
        for (var index = 0; index < barCount; index++)
        {
            var historyIndex = (int)MathF.Round(index / (float)(barCount - 1) * (_levels.Length - 1));
            var envelope = MathF.Sin(index / (float)(barCount - 1) * MathF.PI);
            var barHeight = MathF.Max(2.5f, _levels[historyIndex] * envelope * ((float)size.Height - 7));
            var x = index * slotWidth + (slotWidth - barWidth) / 2;
            var y = center - barHeight / 2;
            var alpha = (byte)(150 + envelope * 105);
            args.DrawingSession.FillRoundedRectangle(
                x, y, barWidth, barHeight, barWidth / 2, barWidth / 2,
                _paused ? Color.FromArgb(alpha, 244, 188, 106) : Color.FromArgb(alpha, 59, 167, 255));
        }
    }

    private void FadeIn()
    {
        var visual = ElementCompositionPreview.GetElementVisual(OverlayRoot);
        visual.Scale = Vector3.One;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            visual.Opacity = 1;
            return;
        }

        var compositor = visual.Compositor;
        visual.Opacity = 0;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = TimeSpan.FromMilliseconds(140);
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1)));
        visual.StartAnimation(nameof(visual.Opacity), opacity);

    }

    private void ConfigureNativeWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(style | WsExNoactivate | WsExToolwindow));
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        NativeWindowAppearance.RemoveOverlayFrame(this);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
