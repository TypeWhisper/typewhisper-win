using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    private const uint MonitorDefaultToNearest = 2;
    private const int EnumCurrentSettings = -1;

    // MONITORINFOEXW: only the size and the device name are read.
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private unsafe struct MonitorInfoEx
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(40)] public fixed ushort Device[32];
    }

    // DEVMODEW for a display: only the size and the refresh rate are used.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint DisplayFrequency;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string device, int mode, ref DisplayMode devMode);

    /// <summary>The refresh rate in Hz of the display that shows most of the window, or 0 if Windows does not report one.</summary>
    internal static unsafe int RefreshRateOf(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return 0;
        var device = new string((char*)info.Device);
        var mode = new DisplayMode { Size = (ushort)sizeof(DisplayMode) };
        // 0 and 1 stand for the hardware's default rate.
        return EnumDisplaySettings(device, EnumCurrentSettings, ref mode) && mode.DisplayFrequency > 1 ? (int)mode.DisplayFrequency : 0;
    }
}
