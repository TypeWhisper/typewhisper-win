using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.WinUI;

// Capture and all clipboard continuations belong to the owning WinUI thread. The caller captures
// HWND/PID at shortcut admission; this adapter never activates a window or uses existing clipboard text.
internal sealed class WindowsSelectedTextCapture(IntPtr ownerHandle)
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    internal async Task<string> CaptureAsync(IntPtr originalWindow, uint originalProcessId, CancellationToken ct = default)
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Selected-text capture must run on the owning UI thread.");
        if (originalWindow == IntPtr.Zero || originalProcessId == 0 || originalProcessId == (uint)Environment.ProcessId)
            throw new InvalidOperationException("Select text in another application before running this workflow.");
        await ClipboardTextInserter.TransactionGate.WaitAsync(ct);
        try
        {
            using var clipboard = new WindowsClipboardTransaction(ownerHandle);
            return await SelectedTextCaptureOperation.RunAsync(new Platform(clipboard, originalWindow, originalProcessId), ct);
        }
        finally { ClipboardTextInserter.TransactionGate.Release(); }
    }

    private sealed class Lease(IClipboardLease inner, uint markerSequence) : ISelectedTextCaptureLease
    {
        internal IClipboardLease Inner { get; } = inner;
        public uint MarkerSequenceNumber => markerSequence;
        public void Dispose() => Inner.Dispose();
    }

    private sealed class Platform(WindowsClipboardTransaction clipboard, IntPtr target, uint processId) : ISelectedTextCapturePlatform
    {
        private static bool ModifiersReleased => !new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }
            .Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
        private bool SameTarget
        {
            get
            {
                if (GetForegroundWindow() != target || !IsWindow(target)) return false;
                GetWindowThreadProcessId(target, out var currentProcess);
                return currentProcess == processId;
            }
        }
        public bool TargetStillCurrent => SameTarget && ModifiersReleased;
        public async Task<bool> WaitModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                if (!SameTarget) return false;
                if (ModifiersReleased) return true;
                await Task.Delay(25, ct);
            }
            return false;
        }
        public async Task<ISelectedTextCaptureLease> BeginTemporaryAsync(string marker, CancellationToken ct)
        {
            var inner = await clipboard.BeginTemporaryTextAsync(marker, ct);
            return new Lease(inner, clipboard.ExpectedSequence(inner));
        }
        public bool OwnsMarker(ISelectedTextCaptureLease lease) => clipboard.IsCurrent(((Lease)lease).Inner);
        public uint SendCopy()
        {
            if (!TargetStillCurrent) return 0;
            Input[] inputs = [Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true)];
            var sent = SendInput(4, inputs, Marshal.SizeOf<Input>());
            if (sent is > 0 and < 4)
            {
                Input[] release = [Key(0x43, true), Key(0x11, true)];
                SendInput(2, release, Marshal.SizeOf<Input>());
            }
            return sent;
        }
        private bool VerifyOwner(IntPtr window)
        {
            if (!TargetStillCurrent || window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out var ownerProcess);
            // Standard editors may publish through a hidden same-process OLE clipboard broker.
            // Bind ownership to the original process and unchanged foreground HWND/PID; this
            // does not lock a particular field or tab within that process.
            return ownerProcess == processId;
        }
        public async Task<SelectedClipboardState> ReadClipboardAsync(CancellationToken ct)
        {
            var state = await clipboard.ReadTextStateAsync(ct, SelectedTextCaptureOperation.MaxInputCharacters + 1);
            return new(state.Text, state.SequenceNumber, VerifyOwner(state.OwnerWindow));
        }
        public bool AcceptCopiedSequence(ISelectedTextCaptureLease lease, SelectedClipboardState state) =>
            state.SourceOwnerVerified && state.SequenceNumber != lease.MarkerSequenceNumber
            && clipboard.TryAcceptCopiedSequence(((Lease)lease).Inner, state.SequenceNumber, VerifyOwner);
        public async Task RestoreAsync(ISelectedTextCaptureLease lease) =>
            _ = await clipboard.RestoreAsync(((Lease)lease).Inner, CancellationToken.None);
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
