using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private IntPtr _previousApp;
    private uint _previousAppProcess;

    private void RememberPreviousApp()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var process);
        if (process == 0 || process == Environment.ProcessId) return;
        _previousApp = window;
        _previousAppProcess = process;
    }

    private void ReturnToPreviousApp()
    {
        GetWindowThreadProcessId(_previousApp, out var process);
        if (_previousApp == IntPtr.Zero || !PreviousAppIsWindow(_previousApp) || process != _previousAppProcess)
        {
            MetricsText.Text = "Open Quick Launch from a text field to return to it here.";
            return;
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        AppWindow.Hide();
        if (PreviousAppIsIconic(_previousApp)) PreviousAppShowWindow(_previousApp, 9);
        // Activating the original app restores its own focused control, including
        // document editors whose caret is not represented by a separate HWND.
        if (!SetForegroundWindow(_previousApp))
        {
            AppWindow.Show();
            Activate();
            MetricsText.Text = "Windows could not activate the previous app. Switch to it and use your dictation shortcut.";
        }
    }

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviousAppIsWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviousAppIsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviousAppShowWindow(IntPtr window, int command);
}
