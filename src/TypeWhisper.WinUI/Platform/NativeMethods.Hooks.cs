using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    // Windows calls the hook through the function pointer of this delegate. The owner keeps the
    // delegate in a field for as long as the hook is installed; a collected delegate would leave
    // the pointer dangling.
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    // KBDLLHOOKSTRUCT as passed to a WH_KEYBOARD_LL hook.
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }

    // MSLLHOOKSTRUCT as passed to a WH_MOUSE_LL hook.
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseHookData { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }

    [LibraryImport("user32.dll")]
    public static partial int GetMessageTime();

    // MSG, for a thread that only pumps messages so its low-level hook gets called.
    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadMessage { public IntPtr Window; public uint Id; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }

    public const uint WM_QUIT = 0x0012;
    public const uint PM_NOREMOVE = 0x0000;

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out ThreadMessage message, IntPtr window, uint first, uint last);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out ThreadMessage message, IntPtr window, uint first, uint last, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    // Delegates are not supported by source-generated marshalling.
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? name);
}
