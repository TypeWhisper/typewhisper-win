using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsCursor = System.Windows.Forms.Cursor;
using FormsScreen = System.Windows.Forms.Screen;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides main window behavior.
/// </summary>
public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly Size DefaultOverlaySize = new(300, 50);
    private static readonly TimeSpan OverlayRecoveryDelay = TimeSpan.FromMilliseconds(700);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly ISettingsService _settings;
    private readonly ViewModels.DictationViewModel _viewModel;
    private readonly DispatcherTimer _overlayRecoveryTimer;
    private OverlayPlacementTarget _currentPlacementTarget = OverlayPlacementTarget.CursorMonitor;

    /// <summary>
    /// Initializes a new instance of the MainWindow class.
    /// </summary>
    public MainWindow(ViewModels.DictationViewModel viewModel, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _settings = settings;
        _overlayRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = OverlayRecoveryDelay
        };
        _overlayRecoveryTimer.Tick += OnOverlayRecoveryTimerTick;
    }

    /// <summary>
    /// Marks the overlay window as non-activating and tool-window styled once the native handle exists.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOverlay(OverlayPlacementTarget.CursorMonitor);
        _settings.SettingsChanged += OnSettingsChanged;
        PropertyChangedEventManager.AddHandler(
            _viewModel,
            OnViewModelPropertyChanged,
            nameof(ViewModels.DictationViewModel.IsOverlayVisible));
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionOverlay();
    }

    /// <summary>
    /// Releases static Windows event subscriptions that otherwise keep the overlay window alive.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        PropertyChangedEventManager.RemoveHandler(
            _viewModel,
            OnViewModelPropertyChanged,
            nameof(ViewModels.DictationViewModel.IsOverlayVisible));
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _overlayRecoveryTimer.Stop();
        _overlayRecoveryTimer.Tick -= OnOverlayRecoveryTimerTick;

        base.OnClosed(e);
    }

    private void OnSettingsChanged(AppSettings settings) =>
        Dispatcher.InvokeAsync(PositionOverlay);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_viewModel.IsOverlayVisible)
            return;

        PositionOverlay(OverlayPlacementTarget.CursorMonitor);
        ReassertTopmost();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        DispatchPrimaryOverlayRecovery();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            DispatchPrimaryOverlayRecovery();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            DispatchPrimaryOverlayRecovery();
    }

    private void DispatchPrimaryOverlayRecovery()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        Dispatcher.InvokeAsync(SchedulePrimaryOverlayRecovery);
    }

    private void SchedulePrimaryOverlayRecovery()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(SchedulePrimaryOverlayRecovery);
            return;
        }

        _overlayRecoveryTimer.Stop();
        _overlayRecoveryTimer.Start();
    }

    private void OnOverlayRecoveryTimerTick(object? sender, EventArgs e)
    {
        _overlayRecoveryTimer.Stop();
        RecoverOverlayToPrimary();
    }

    private void RecoverOverlayToPrimary()
    {
        PositionOverlay(OverlayPlacementTarget.PrimaryMonitor);
        ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        Topmost = false;
        Topmost = true;
    }

    private void PositionOverlay() =>
        PositionOverlay(_currentPlacementTarget);

    private void PositionOverlay(OverlayPlacementTarget target)
    {
        _currentPlacementTarget = target;
        var point = OverlayPlacementCalculator.Calculate(
            GetWorkArea(target),
            new Size(ActualWidth, ActualHeight),
            _settings.Current.OverlayPosition,
            DefaultOverlaySize);

        Left = point.X;
        Top = point.Y;
    }

    private Rect GetWorkArea(OverlayPlacementTarget target) =>
        OverlayPlacementCalculator.SelectWorkArea(
            target,
            TryGetScreenWorkArea(FormsScreen.FromPoint(FormsCursor.Position)),
            TryGetScreenWorkArea(FormsScreen.PrimaryScreen),
            SystemParameters.WorkArea);

    private Rect? TryGetScreenWorkArea(FormsScreen? screen) =>
        screen is null ? null : DeviceRectToWpf(screen.WorkingArea);

    private Rect DeviceRectToWpf(DrawingRectangle rectangle)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(rectangle.Left, rectangle.Top));
        var bottomRight = transform.Transform(new Point(rectangle.Right, rectangle.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
