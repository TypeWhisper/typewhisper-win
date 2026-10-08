using System.Runtime.InteropServices;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed class ClipboardTextInserter(IntPtr owner) : IDisposable
{
    private readonly WindowsClipboardTransaction _clipboard = new(owner);
    private Task _restored = Task.CompletedTask;
    internal static SemaphoreSlim TransactionGate { get; } = new(1, 1);
    /// <summary>Completes once the latest paste restored the previous clipboard. Never faults.</summary>
    internal Task Restored => _restored;
    /// <summary>Returns once Ctrl+V was sent. The clipboard stays gated until <see cref="Restored"/> completes.</summary>
    internal async Task<bool> InsertAsync(string text, IntPtr target, Func<bool>? verifyField = null, Func<Task<bool>>? verifyFieldAsync = null)
    {
        var dictation = AppDiagnostics.CurrentDictation;
        await TransactionGate.WaitAsync();
        var releaseNow = true;
        try
        {
            var result = await ClipboardPasteOperation.RunAsync(new Platform(_clipboard, target, verifyField, verifyFieldAsync), text);
            AppDiagnostics.Write(result.Inserted ? "clipboard.paste.sent" : "clipboard.paste.rejected");
            _restored = ReleaseAfterRestoreAsync(result.Restored, dictation);
            releaseNow = false;
            return result.Inserted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { AppDiagnostics.Write("clipboard.paste.exception", ex); throw; }
        finally { if (releaseNow) TransactionGate.Release(); }
    }
    private static async Task ReleaseAfterRestoreAsync(Task restored, AppDiagnostics.DictationContext dictation)
    {
        try { await restored; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The paste was already sent; a failed restore must not turn it into a delivery failure.
            // The restore usually ends after the dictation, so log it under the dictation that pasted.
            AppDiagnostics.Write(dictation, "clipboard.restore.exception", ex);
        }
        finally { TransactionGate.Release(); }
    }
    public void Dispose() => _clipboard.Dispose();

    private sealed class Platform(WindowsClipboardTransaction clipboard, IntPtr target, Func<bool>? verifyField, Func<Task<bool>>? verifyFieldAsync) : IClipboardPastePlatform
    {
        private IClipboardLease? _lease;
        public Task<bool> VerifyFieldAsync() => verifyFieldAsync?.Invoke() ?? Task.FromResult(true);
        public bool CanPaste => target != IntPtr.Zero && NativeMethods.GetForegroundWindow() == target && (verifyField?.Invoke() ?? true)
            && !new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);
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
            NativeMethods.Input[] inputs = [Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true)];
            var sent = NativeMethods.SendInput(4, inputs, Marshal.SizeOf<NativeMethods.Input>());
            if (sent is > 0 and < 4)
            {
                NativeMethods.Input[] release = [Key(0x56, true), Key(0x11, true)];
                NativeMethods.SendInput(2, release, Marshal.SizeOf<NativeMethods.Input>());
            }
            return sent;
        }
    }
    private static NativeMethods.Input Key(ushort key, bool up) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { Key = key, Flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0u, Extra = OwnKeyboardInput.Marker } }
    };
}
