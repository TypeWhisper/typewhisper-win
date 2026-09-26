using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
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
    private readonly TranscriptScrollFollow _scrollFollow = new();
    private int _lastWordCount = -1;
    private int _pixelWidth = OverlayWindow.WindowWidth;
    private double _scale = 1;
    private bool _paused;
    private readonly Func<string>? _liveText;
    private bool _opensDown;
    private int _recordingHeight;
    private bool _floating;
    private bool _headerHovered;
    private bool _hasAnchor;
    private LiveTextPosition? _floatingPosition;
    private PointInt32? _dragStart;
    private PointInt32 _dragWindowStart;
    private PointInt32? _resizeStart;
    private LiveTextBounds _resizeWindowStart;
    private LiveTextBounds _resizeWorkArea;
    private LiveTextResizeEdge _resizeEdge;
    private double _resizeScale = 1;
    private ResizeHandleGrid? _activeResizeHandle;
    private int ExpandedHeight => _floating ? (int)Math.Round(_floatingPosition?.Height ?? 220) : FullHeight;
    private static string PositionPath => WinUIProfile.DataPath("live-text-position.json");

    internal event EventHandler? Collapsed;

    internal TranscriptPreviewWindow(Func<string>? liveText = null)
    {
        _liveText = liveText;
        InitializeComponent();
        InitializeResizeHandles();
        TranscriptSourceLabel.Text = liveText is null ? "DEMO TEXT" : "LIVE TEXT";
        NativeWindowAppearance.ApplyAppTitleBar(this);
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
            FinishDragging();
            FinishResizing();
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
        _hasAnchor = true;
        _opensDown = opensDown;
        _recordingHeight = recordingHeight;
        TranscriptRoot.CornerRadius = _floating ? new CornerRadius(14) : opensDown ? new CornerRadius(0, 0, 14, 14) : new CornerRadius(14, 14, 0, 0);
        _recordingPosition = recordingPosition;
        _pixelWidth = pixelWidth;
        _scale = scale;
        ApplyWindowBounds(Math.Max(1, (int)Math.Round(ExpandedHeight * _expansion)));
    }

    internal void SetFloating(bool floating)
    {
        if (_floating == floating) return;
        FinishDragging();
        FinishResizing();
        _floating = floating;
        TranscriptHeader.ReleasePointerCaptures();
        ResizeHandles.Visibility = floating ? Visibility.Visible : Visibility.Collapsed;
        if (floating) _floatingPosition = LiveTextPlacement.Read(PositionPath);
        TranscriptHeader.SetDraggable(floating);
        UpdateDragAppearance();
        DragHint.Visibility = floating ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(TranscriptHeader, floating ? "Drag to move live text" : null);
        if (_hasAnchor) SetAnchor(_recordingPosition, _pixelWidth, _scale, _opensDown, _recordingHeight);
    }

    internal LiveTextPreviewFrame? FloatingPlacement { get; private set; }
    internal event Action<LiveTextPreviewFrame>? FloatingPlacementChanged;

    internal void SetFloatingSize(double width, double height)
    {
        if (!_floating || _floatingPosition is null || !double.IsFinite(width) || !double.IsFinite(height)) return;
        FinishDragging();
        FinishResizing();
        _floatingPosition = _floatingPosition with
        {
            Width = Math.Clamp(width, LiveTextPlacement.MinimumWidth, 8192),
            Height = Math.Clamp(height, LiveTextPlacement.MinimumHeight, 8192)
        };
        ApplyWindowBounds(Math.Max(1, (int)Math.Round(ExpandedHeight * _expansion)));
        SaveFloatingPlacement();
    }

    private void TranscriptHeader_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _headerHovered = true;
        UpdateDragAppearance();
    }

    private void TranscriptHeader_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _headerHovered = false;
        UpdateDragAppearance();
    }

    private void UpdateDragAppearance()
    {
        var highlighted = _floating && (_headerHovered || _dragStart is not null);
        TranscriptHeader.Background = highlighted
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ElevatedBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        DragHint.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[highlighted ? "AccentBrush" : "MutedBrush"];
    }

    private void TranscriptHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_floating || _resizeStart is not null || _expansion < 0.999 || !e.GetCurrentPoint(TranscriptHeader).Properties.IsLeftButtonPressed
            || !GetCursorPos(out var point) || !TranscriptHeader.CapturePointer(e.Pointer)) return;
        _dragStart = point;
        _dragWindowStart = AppWindow.Position;
        UpdateDragAppearance();
        e.Handled = true;
    }

    private void TranscriptHeader_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start || !GetCursorPos(out var point)) return;
        _floatingPosition = (_floatingPosition ?? new LiveTextPosition(_dragWindowStart.X, _dragWindowStart.Y)) with
        { X = _dragWindowStart.X + point.X - start.X, Y = _dragWindowStart.Y + point.Y - start.Y };
        ApplyWindowBounds(Math.Max(1, (int)Math.Round(ExpandedHeight * _expansion)));
        e.Handled = true;
    }

    private void TranscriptHeader_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishDragging();
        TranscriptHeader.ReleasePointerCaptures();
    }

    private void TranscriptHeader_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => FinishDragging();

    private void FinishDragging()
    {
        if (_dragStart is null) return;
        _dragStart = null;
        UpdateDragAppearance();
        SaveFloatingPlacement();
    }

    private void SaveFloatingPlacement()
    {
        if (_floatingPosition is null) return;
        try { LiveTextPlacement.Save(PositionPath, _floatingPosition); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Debug.WriteLine($"Could not save live-text placement: {ex.GetType().Name}"); }
    }

    private void InitializeResizeHandles()
    {
        AddResizeHandle(LiveTextResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, 8, double.NaN, InputSystemCursorShape.SizeWestEast);
        AddResizeHandle(LiveTextResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, InputSystemCursorShape.SizeWestEast);
        AddResizeHandle(LiveTextResizeEdge.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 8, InputSystemCursorShape.SizeNorthSouth);
        AddResizeHandle(LiveTextResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, InputSystemCursorShape.SizeNorthSouth);
        AddResizeHandle(LiveTextResizeEdge.Top | LiveTextResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Top, 18, 18, InputSystemCursorShape.SizeNorthwestSoutheast);
        AddResizeHandle(LiveTextResizeEdge.Top | LiveTextResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Top, 18, 18, InputSystemCursorShape.SizeNortheastSouthwest);
        AddResizeHandle(LiveTextResizeEdge.Bottom | LiveTextResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Bottom, 18, 18, InputSystemCursorShape.SizeNortheastSouthwest);
        AddResizeHandle(LiveTextResizeEdge.Bottom | LiveTextResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Bottom, 18, 18, InputSystemCursorShape.SizeNorthwestSoutheast);
    }

    private void AddResizeHandle(LiveTextResizeEdge edge, HorizontalAlignment horizontal, VerticalAlignment vertical,
        double width, double height, InputSystemCursorShape cursor)
    {
        var handle = new ResizeHandleGrid(cursor)
        {
            Tag = edge, Width = width, Height = height,
            HorizontalAlignment = horizontal, VerticalAlignment = vertical,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle, $"Resize live text: {edge}");
        ToolTipService.SetToolTip(handle, "Drag to resize live text");
        handle.PointerPressed += ResizeHandle_PointerPressed;
        handle.PointerMoved += ResizeHandle_PointerMoved;
        handle.PointerReleased += (_, _) => FinishResizing();
        handle.PointerCaptureLost += (_, _) => FinishResizing();
        ResizeHandles.Children.Add(handle);
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_floating || _expansion < 0.999 || _dragStart is not null || _resizeStart is not null
            || sender is not ResizeHandleGrid handle
            || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed
            || !GetCursorPos(out var point) || !handle.CapturePointer(e.Pointer)) return;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _resizeStart = point;
        _resizeEdge = (LiveTextResizeEdge)handle.Tag;
        _resizeWindowStart = new(position.X, position.Y, size.Width, size.Height);
        _resizeWorkArea = new(work.X, work.Y, work.Width, work.Height);
        _resizeScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (_resizeScale <= 0) _resizeScale = _scale;
        _activeResizeHandle = handle;
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeStart is not { } start || !GetCursorPos(out var point)) return;
        var bounds = LiveTextPlacement.Resize(_resizeWindowStart, _resizeEdge, point.X - start.X, point.Y - start.Y,
            (int)Math.Round(LiveTextPlacement.MinimumWidth * _resizeScale),
            (int)Math.Round(LiveTextPlacement.MinimumHeight * _resizeScale), _resizeWorkArea);
        _floatingPosition = new(bounds.X, bounds.Y, bounds.Width / _resizeScale, bounds.Height / _resizeScale);
        ApplyWindowBounds(ExpandedHeight);
        e.Handled = true;
    }

    private void FinishResizing()
    {
        if (_resizeStart is null) return;
        _resizeStart = null;
        var handle = _activeResizeHandle;
        _activeResizeHandle = null;
        handle?.ReleasePointerCaptures();
        SaveFloatingPlacement();
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

    internal void SetTextSize(double size)
    {
        if (!double.IsFinite(size) || size is < 10 or > 18) return;
        TranscriptText.FontSize = size;
        TranscriptText.LineHeight = size * 1.4;
    }

    internal void HideImmediately()
    {
        FinishDragging();
        TranscriptHeader.ReleasePointerCaptures();
        FinishResizing();
        _timer.Stop();
        _animationClock.Reset();
        _streamClock.Reset();
        _animationTarget = _expansion = 0;
        AppWindow.Hide();
        Collapsed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetExpanded(bool expanded, PointInt32 recordingPosition)
    {
        if (!expanded)
        {
            FinishDragging();
            TranscriptHeader.ReleasePointerCaptures();
            FinishResizing();
        }
        _recordingPosition = recordingPosition;
        var target = expanded ? 1d : 0d;
        if (Math.Abs(_expansion - target) < 0.001 && !_animationClock.IsRunning)
            return;
        if (_animationClock.IsRunning && Math.Abs(_animationTarget - target) < 0.001)
            return;

        if (expanded)
        {
            if (_floating) _floatingPosition = LiveTextPlacement.Read(PositionPath) ?? _floatingPosition;
            AppWindow.Show(activateWindow: false);
            _streamClock.Restart();
            if (_paused) _streamClock.Stop();
            _scrollFollow.Reset();
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

        var height = Math.Max(1, (int)Math.Round(ExpandedHeight * _expansion));
        ApplyWindowBounds(height);

        var visual = ElementCompositionPreview.GetElementVisual(TranscriptRoot);
        visual.Opacity = (float)Math.Clamp(_expansion * 1.35, 0, 1);
        visual.Offset = new Vector3(0, (float)((1 - _expansion) * (_opensDown ? -10 : 10)), 0);

        if (linear < 1)
            return;

        _expansion = _animationTarget;
        _animationClock.Stop();
        ApplyWindowBounds(_expansion > 0 ? ExpandedHeight : 1);

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
        if (_floating)
        {
            var desired = _floatingPosition ?? new LiveTextPosition(_recordingPosition.X,
                _opensDown ? _recordingPosition.Y + _recordingHeight + 12 : _recordingPosition.Y - (int)(ExpandedHeight * _scale) - 12);
            var area = DisplayArea.GetFromPoint(new PointInt32(desired.X, desired.Y), DisplayAreaFallback.Nearest);
            var work = area.WorkArea;
            var currentArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (currentArea?.DisplayId != area.DisplayId)
                AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
            if (scale <= 0) scale = _scale;
            var width = Math.Min((int)Math.Round(desired.Width * scale), work.Width);
            var fullHeight = Math.Min((int)Math.Round(desired.Height * scale), work.Height);
            var position = LiveTextPlacement.Clamp(desired, width, fullHeight, work.X, work.Y, work.Width, work.Height);
            _floatingPosition = position;
            AppWindow.MoveAndResize(new RectInt32(position.X, position.Y, width, Math.Min(fullHeight, Math.Max(1, (int)Math.Round(height * scale)))));
            NativeWindowAppearance.RemoveOverlayFrame(this);
            var frame = new LiveTextPreviewFrame(new(position.X, position.Y, width, fullHeight), new(work.X, work.Y, work.Width, work.Height), position.Width, position.Height);
            if (frame != FloatingPlacement)
            {
                FloatingPlacement = frame;
                FloatingPlacementChanged?.Invoke(frame);
            }
            return;
        }
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
        if (_liveText is not null)
        {
            var text = _liveText();
            if (TranscriptText.Text != text)
                TranscriptText.Text = text;
            return;
        }
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

        if (wordCount >= DemoTranscriptWords.Length)
            _streamClock.Stop();
    }

    // Runs after layout, when the new text or viewport size is already reflected in ScrollableHeight.
    private void KeepTranscriptEndVisible(object sender, SizeChangedEventArgs e)
    {
        if (_scrollFollow.Following)
            TranscriptScrollViewer.ChangeView(null, TranscriptScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void TranscriptScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) =>
        _scrollFollow.ViewChanged(TranscriptScrollViewer.VerticalOffset, TranscriptScrollViewer.ScrollableHeight);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
