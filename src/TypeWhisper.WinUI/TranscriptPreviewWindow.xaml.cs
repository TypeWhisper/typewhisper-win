using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

public sealed partial class TranscriptPreviewWindow : Window
{
    private const int FullHeight = 146;
    private const int SeamUnderlap = 1;
    private const double AnimationDurationMilliseconds = 260;
    private const int GwlExstyle = -20;
    private const long WsExNoactivate = 0x08000000L;
    private const long WsExToolwindow = 0x00000080L;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private static readonly string[] DemoTranscriptWords =
        ("Wenn ich spreche, klappt das Overlay auf und zeigt den Text direkt während der Aufnahme an. "
         + "Auch längere Gedanken bleiben lesbar: Der Inhalt läuft automatisch mit, solange ich am Ende bleibe. "
         + "Scrolle ich nach oben, wird meine Position respektiert, damit ich frühere Sätze in Ruhe nachlesen kann. "
         + "Die Vorschau darf dabei mehrere Absätze aufnehmen, ohne den festen Aufnahmeindikator auch nur um einen Pixel zu bewegen. "
         + "So kann ich den entstehenden Text kontrollieren, einzelne Formulierungen noch einmal ansehen und trotzdem jederzeit erkennen, dass die Aufnahme weiterläuft. "
         + "Sobald ich wieder ganz nach unten gehe, folgt die Vorschau erneut dem aktuellen Text. Das hier ist bewusst ein längerer lokaler Dummytext für den Scroll-Test.")
        .Split(' ');

    private readonly Stopwatch _animationClock = new();
    private readonly Stopwatch _streamClock = new();
    private readonly DispatcherTimer _timer;
    private PointInt32 _recordingPosition;
    private double _animationFrom;
    private double _animationTarget;
    private double _expansion;
    private bool _followTranscript = true;
    private int _lastWordCount = -1;
    private int _pixelWidth = OverlayWindow.WindowWidth;
    private double _scale = 1;
    private bool _paused;
    private bool _opensDown;
    private int _recordingHeight;

    internal event EventHandler? Collapsed;

    internal TranscriptPreviewWindow()
    {
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
        AppWindow.Resize(new SizeInt32(OverlayWindow.WindowWidth, 1 + SeamUnderlap));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Timer_Tick;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            SystemBackdrop = null;
        };
    }

    internal void ShowWithoutTakingFocus(PointInt32 recordingPosition)
    {
        _recordingPosition = recordingPosition;
        ConfigureNativeWindow();
        ApplyWindowBounds(1);
        AppWindow.Show(activateWindow: false);
        NativeWindowAppearance.RemoveOverlayFrame(this);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => NativeWindowAppearance.RemoveOverlayFrame(this));
    }

    internal void SetAnchor(PointInt32 recordingPosition, int pixelWidth, double scale, bool opensDown = false, int recordingHeight = 0)
    {
        _opensDown = opensDown;
        _recordingHeight = recordingHeight;
        TranscriptRoot.CornerRadius = opensDown ? new CornerRadius(0, 0, 14, 14) : new CornerRadius(14, 14, 0, 0);
        _recordingPosition = recordingPosition;
        _pixelWidth = pixelWidth;
        _scale = scale;
        ApplyWindowBounds(Math.Max(1, (int)Math.Round(FullHeight * _expansion)));
    }

    internal void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
            _streamClock.Stop();
        else if (_expansion > 0 && _lastWordCount < DemoTranscriptWords.Length)
            _streamClock.Start();
        if (!paused && _expansion > 0) _timer.Start();
    }

    internal void HideImmediately()
    {
        _timer.Stop();
        _animationClock.Reset();
        _streamClock.Reset();
        _animationTarget = _expansion = 0;
        AppWindow.Hide();
        Collapsed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetExpanded(bool expanded, PointInt32 recordingPosition)
    {
        _recordingPosition = recordingPosition;
        var target = expanded ? 1d : 0d;
        if (Math.Abs(_expansion - target) < 0.001 && !_animationClock.IsRunning)
            return;
        if (_animationClock.IsRunning && Math.Abs(_animationTarget - target) < 0.001)
            return;

        if (expanded)
        {
            AppWindow.Show(activateWindow: false);
            _streamClock.Restart();
            if (_paused) _streamClock.Stop();
            _followTranscript = true;
            _lastWordCount = -1;
            TranscriptText.Text = string.Empty;
        }

        _animationFrom = _expansion;
        _animationTarget = target;
        _animationClock.Restart();
        _timer.Start();
    }

    private void Timer_Tick(object? sender, object e)
    {
        UpdateAnimation();
        UpdateTranscriptContent();

        if (!_animationClock.IsRunning && !_streamClock.IsRunning)
            _timer.Stop();
    }

    private void UpdateAnimation()
    {
        if (!_animationClock.IsRunning)
            return;

        var linear = !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ? 1 : Math.Clamp(
            _animationClock.Elapsed.TotalMilliseconds / AnimationDurationMilliseconds,
            0,
            1);
        var eased = 1 - Math.Pow(1 - linear, 3);
        _expansion = _animationFrom + (_animationTarget - _animationFrom) * eased;

        var height = Math.Max(1, (int)Math.Round(FullHeight * _expansion));
        ApplyWindowBounds(height);

        var visual = ElementCompositionPreview.GetElementVisual(TranscriptRoot);
        visual.Opacity = (float)Math.Clamp(_expansion * 1.35, 0, 1);
        visual.Offset = new Vector3(0, (float)((1 - _expansion) * (_opensDown ? -10 : 10)), 0);

        if (linear < 1)
            return;

        _expansion = _animationTarget;
        _animationClock.Stop();
        ApplyWindowBounds(_expansion > 0 ? FullHeight : 1);

        if (_expansion <= 0)
        {
            _streamClock.Reset();
            TranscriptText.Text = string.Empty;
            AppWindow.Hide();
            Collapsed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyWindowBounds(int height)
    {
        var pixelHeight = Math.Max(1, (int)Math.Round(height * _scale));
        AppWindow.MoveAndResize(new RectInt32(
            _recordingPosition.X,
            _opensDown ? _recordingPosition.Y + _recordingHeight - SeamUnderlap : _recordingPosition.Y - pixelHeight,
            _pixelWidth,
            pixelHeight + SeamUnderlap));
        NativeWindowAppearance.RemoveOverlayFrame(this);
    }

    private void UpdateTranscriptContent()
    {
        if (!_streamClock.IsRunning)
            return;

        var wordCount = Math.Clamp(
            1 + (int)(_streamClock.Elapsed.TotalMilliseconds / 105),
            1,
            DemoTranscriptWords.Length);
        if (wordCount == _lastWordCount)
            return;

        _lastWordCount = wordCount;
        TranscriptText.Text = string.Join(" ", DemoTranscriptWords, 0, wordCount);

        if (_followTranscript)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TranscriptScrollViewer.UpdateLayout();
                TranscriptScrollViewer.ChangeView(
                    horizontalOffset: null,
                    verticalOffset: TranscriptScrollViewer.ScrollableHeight,
                    zoomFactor: null,
                    disableAnimation: true);
            });
        }

        if (wordCount >= DemoTranscriptWords.Length)
            _streamClock.Stop();
    }

    private void TranscriptScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        _followTranscript = TranscriptScrollViewer.ScrollableHeight
            - TranscriptScrollViewer.VerticalOffset <= 12;
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
}
