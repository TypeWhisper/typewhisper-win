using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    // INPUT and its union. The layouts match the native structs byte for byte; SendInput rejects
    // a size that differs from what Windows expects.
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, Input[] inputs, int size);
}
