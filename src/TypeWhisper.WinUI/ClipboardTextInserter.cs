using System.Runtime.InteropServices;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.WinUI;

internal sealed class ClipboardTextInserter(IntPtr owner) : IDisposable
{
    private readonly WindowsClipboardTransaction _clipboard = new(owner);
    internal Task<bool> InsertAsync(string text, IntPtr target) =>
        ClipboardPasteOperation.RunAsync(new Platform(_clipboard, target), text);
    public void Dispose() => _clipboard.Dispose();

    private sealed class Platform(WindowsClipboardTransaction clipboard, IntPtr target) : IClipboardPastePlatform
    {
        private IClipboardLease? _lease;
        public bool CanPaste => target != IntPtr.Zero && GetForegroundWindow() == target
            && !new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
        public bool ClipboardIsOwned => _lease is not null && clipboard.IsCurrent(_lease);
        public async Task<IDisposable> BeginAsync(string text)
        {
            var lease = await clipboard.BeginTemporaryTextAsync(text, CancellationToken.None);
            _lease = lease;
            return lease;
        }
        public Task RestoreAsync(IDisposable lease) => clipboard.RestoreAsync((IClipboardLease)lease, CancellationToken.None);
        public Task WaitForPasteAsync() => Task.Delay(500);
        public uint SendPaste()
        {
            Input[] inputs = [Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true)];
            var sent = SendInput(4, inputs, Marshal.SizeOf<Input>());
            if (sent is > 0 and < 4)
            {
                Input[] release = [Key(0x56, true), Key(0x11, true)];
                SendInput(2, release, Marshal.SizeOf<Input>());
            }
            return sent;
        }
    }
    private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
