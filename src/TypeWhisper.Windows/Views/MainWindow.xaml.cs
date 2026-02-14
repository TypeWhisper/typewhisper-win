using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly ISettingsService _settings;

    public MainWindow(DictationViewModel viewModel, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Make overlay non-focusable so it never steals focus from the active app
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOverlay();
        _settings.SettingsChanged += _ => Dispatcher.Invoke(PositionOverlay);
    }

    private void PositionOverlay()
    {
        var workArea = SystemParameters.WorkArea;

        Left = (workArea.Width - Width) / 2 + workArea.Left;

        if (_settings.Current.OverlayPosition == OverlayPosition.Top)
            Top = workArea.Top + 20;
        else
            Top = workArea.Bottom - Height - 20;
    }
}
