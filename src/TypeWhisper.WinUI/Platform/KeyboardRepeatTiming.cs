using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

// How long a held key can stay quiet before Windows repeats it, including the accessibility
// FilterKeys timing, which allows repeat delays of up to 20 seconds.
internal static partial class KeyboardRepeatTiming
{
    private const uint GetKeyboardDelay = 0x16, GetFilterKeys = 0x32, FilterKeysOn = 0x1;
    private const long RefreshMilliseconds = 5000;
    private static long _readAt = long.MinValue;
    private static long _longestGap = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys { public uint Size, Flags, WaitMilliseconds, DelayMilliseconds, RepeatMilliseconds, BounceMilliseconds; }

    /// <summary>The longest gap between two key-downs of a held key. Read on the hook's thread and cached briefly.</summary>
    internal static long LongestGapMilliseconds
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - _readAt < RefreshMilliseconds) return _longestGap;
            _readAt = now;
            var filter = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
            if (SystemParametersInfo(GetFilterKeys, filter.Size, ref filter, 0) && (filter.Flags & FilterKeysOn) != 0)
                return _longestGap = Math.Max(filter.DelayMilliseconds, filter.RepeatMilliseconds);
            // Without FilterKeys the delay is 0-3 (250-1000 ms); the repeat interval is always shorter.
            uint delay = 3;
            return _longestGap = SystemParametersInfo(GetKeyboardDelay, 0, ref delay, 0) ? (Math.Min(delay, 3u) + 1) * 250 : 1000;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref FilterKeys value, uint update);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint update);
}
