using System.Runtime.InteropServices;

namespace TypeWhisper.UiAutomation;

internal static class DpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    public static void EnablePerMonitorV2()
    {
        if (OperatingSystem.IsWindows())
            _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);
}
