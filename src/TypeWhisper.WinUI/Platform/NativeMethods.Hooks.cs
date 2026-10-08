using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;

    // Windows calls the hook through the function pointer of this delegate. The owner keeps the
    // delegate in a field for as long as the hook is installed; a collected delegate would leave
    // the pointer dangling.
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    // KBDLLHOOKSTRUCT as passed to a WH_KEYBOARD_LL hook.
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }

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
