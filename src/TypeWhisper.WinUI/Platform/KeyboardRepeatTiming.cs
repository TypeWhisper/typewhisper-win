using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

// How long a held key can stay quiet before Windows repeats it, including the accessibility
// FilterKeys timing, which allows repeat delays of up to 20 seconds or no repeat at all.
internal static partial class KeyboardRepeatTiming
{
    private const uint GetKeyboardDelay = 0x16, GetFilterKeys = 0x32, FilterKeysOn = 0x1;
    private const long RefreshMilliseconds = 5000;
    private static bool _read;
    private static long _readAt;
    private static long _longestGap = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys { public uint Size, Flags, WaitMilliseconds, DelayMilliseconds, RepeatMilliseconds, BounceMilliseconds; }

    /// <summary>The longest gap between two key-downs of a held key, or long.MaxValue when keys never repeat.
    /// Read on the hook's thread and cached briefly.</summary>
    internal static long LongestGapMilliseconds
    {
        get
        {
            var now = Environment.TickCount64;
            if (_read && now - _readAt < RefreshMilliseconds) return _longestGap;
            _read = true;
            _readAt = now;
            var filter = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
            var filterOn = SystemParametersInfo(GetFilterKeys, filter.Size, ref filter, 0) && (filter.Flags & FilterKeysOn) != 0;
            uint delay = 3;
            if (!SystemParametersInfo(GetKeyboardDelay, 0, ref delay, 0)) delay = 3;
            return _longestGap = HybridHotkeyRepeat.LongestGap(filterOn, filter.DelayMilliseconds, filter.RepeatMilliseconds, delay);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref FilterKeys value, uint update);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint update);
}
