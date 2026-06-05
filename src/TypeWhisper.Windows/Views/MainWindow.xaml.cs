using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides main window behavior.
/// </summary>
public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        /// <summary>
        /// Gets the cb size.
        /// </summary>
        public int cbSize;
        /// <summary>
        /// Gets the rc monitor.
        /// </summary>
        public RECT rcMonitor;
        /// <summary>
        /// Gets the rc work.
        /// </summary>
        public RECT rcWork;
        /// <summary>
        /// Gets or sets the Win32 flags field.
        /// </summary>
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private readonly ISettingsService _settings;

    /// <summary>
    /// Initializes a new instance of the MainWindow class.
    /// </summary>
    public MainWindow(ViewModels.DictationViewModel viewModel, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
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
        PositionOverlay();
        _settings.SettingsChanged += _ => Dispatcher.Invoke(PositionOverlay);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionOverlay();
    }

    private void PositionOverlay()
    {
        // Get the monitor where the cursor is
        GetCursorPos(out var cursor);
        var hMonitor = MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);

        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(hMonitor, ref mi))
        {
            var fallback = SystemParameters.WorkArea;
            Left = fallback.Left + (fallback.Width - ActualWidth) / 2;
            Top = _settings.Current.OverlayPosition == OverlayPosition.Top
                ? fallback.Top
                : fallback.Bottom - ActualHeight;
            return;
        }

        // Physical pixels to WPF DIPs
        var source = PresentationSource.FromVisual(this);
        var dpiToWpfX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var dpiToWpfY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        var workLeft = mi.rcWork.Left * dpiToWpfX;
        var workTop = mi.rcWork.Top * dpiToWpfY;
        var workWidth = (mi.rcWork.Right - mi.rcWork.Left) * dpiToWpfX;
        var workBottom = mi.rcWork.Bottom * dpiToWpfY;

        var width = ActualWidth > 0 ? ActualWidth : 300;
        var height = ActualHeight > 0 ? ActualHeight : 50;

        Left = workLeft + (workWidth - width) / 2;

        if (_settings.Current.OverlayPosition == OverlayPosition.Top)
            Top = workTop;
        else
            Top = workBottom - height;
    }
}
