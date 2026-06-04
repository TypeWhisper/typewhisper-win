using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides recent transcriptions palette window behavior.
/// </summary>
public partial class RecentTranscriptionsPaletteWindow : Window
{
    private readonly RecentTranscriptionsPaletteViewModel _viewModel;
    private bool _isSelecting;
    private bool _isClosing;

    /// <summary>
    /// Initializes a new instance of the RecentTranscriptionsPaletteWindow class.
    /// </summary>
    public RecentTranscriptionsPaletteWindow(RecentTranscriptionsPaletteViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnActiveScreen();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isSelecting)
            Dispatcher.BeginInvoke(RequestClose);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                EntriesList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                EntriesList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                SelectAndClose(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
                RequestClose();
                e.Handled = true;
                break;
        }
    }

    private void Entry_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentTranscriptionPaletteItem item)
        {
            SelectAndClose(item);
            e.Handled = true;
        }
    }

    private void SelectAndClose(RecentTranscriptionPaletteItem? item)
    {
        if (item is null)
            return;

        _isSelecting = true;
        RequestClose();
        _viewModel.Select(item);
    }

    /// <summary>
    /// Performs request close.
    /// </summary>
    public void RequestClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        Close();
    }

    private void PositionOnActiveScreen()
    {
        if (!GetCursorPos(out var cursor))
        {
            CenterOnWorkArea(SystemParameters.WorkArea);
            return;
        }

        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
        {
            CenterOnWorkArea(SystemParameters.WorkArea);
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var dpiToWpfX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var dpiToWpfY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        var left = info.rcWork.Left * dpiToWpfX;
        var top = info.rcWork.Top * dpiToWpfY;
        var width = (info.rcWork.Right - info.rcWork.Left) * dpiToWpfX;
        var height = (info.rcWork.Bottom - info.rcWork.Top) * dpiToWpfY;

        Left = left + (width - Width) / 2;
        Top = top + (height - Height) / 2 - 40;
    }

    private void CenterOnWorkArea(Rect workArea)
    {
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2 - 40;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point lpPoint);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        /// <summary>
        /// Gets the x.
        /// </summary>
        public int X;
        /// <summary>
        /// Gets the y.
        /// </summary>
        public int Y;
    }

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
        public RectNative rcMonitor;
        /// <summary>
        /// Gets the rc work.
        /// </summary>
        public RectNative rcWork;
        /// <summary>
        /// Gets or sets the Win32 flags field.
        /// </summary>
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        /// <summary>
        /// Gets the left.
        /// </summary>
        public int Left;
        /// <summary>
        /// Gets the top.
        /// </summary>
        public int Top;
        /// <summary>
        /// Gets the right.
        /// </summary>
        public int Right;
        /// <summary>
        /// Gets the bottom.
        /// </summary>
        public int Bottom;
    }
}
