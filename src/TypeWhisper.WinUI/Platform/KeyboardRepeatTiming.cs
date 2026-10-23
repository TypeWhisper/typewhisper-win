using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

// How long a held key can stay quiet before Windows repeats it, including the accessibility
// FilterKeys timing, which allows repeat delays of up to 20 seconds or no repeat at all.
internal static partial class KeyboardRepeatTiming
{
    private const uint GetKeyboardDelay = 0x16, GetFilterKeys = 0x32, FilterKeysOn = 0x1;
    private const long LongestFilterKeysDelay = 20000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys { public uint Size, Flags, WaitMilliseconds, DelayMilliseconds, RepeatMilliseconds, BounceMilliseconds; }

    /// <summary>The longest gap between two key-downs of a held key, or long.MaxValue when keys never repeat.
    /// Read for every key event, so a FilterKeys change applies to the next key; both reads take microseconds.</summary>
    internal static long LongestGapMilliseconds
    {
        get
        {
            var filter = new FilterKeys { Size = (uint)Marshal.SizeOf<FilterKeys>() };
            // FilterKeys might be on with slow repeats; assume its longest delay rather than the normal timing.
            if (!SystemParametersInfo(GetFilterKeys, filter.Size, ref filter, 0)) return LongestFilterKeysDelay;
            uint delay = 3;
            if (!SystemParametersInfo(GetKeyboardDelay, 0, ref delay, 0)) delay = 3;
            return HybridHotkeyRepeat.LongestGap((filter.Flags & FilterKeysOn) != 0, filter.DelayMilliseconds, filter.RepeatMilliseconds, delay);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref FilterKeys value, uint update);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint update);
}
