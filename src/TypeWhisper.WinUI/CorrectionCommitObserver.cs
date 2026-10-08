using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed class CorrectionCommitObserver(DispatcherQueue dispatcher, IntPtr target) : ITargetAppCorrectionCommitObserver
{
    private NativeMethods.HookProc? _callback;
    private IntPtr _handle;
    private int _signal;
    public void Start() => OnUI(() =>
    {
        Interlocked.Exchange(ref _signal, 0);
        _callback = (code, message, data) =>
        {
            if (code >= 0 && (message == 0x100 || message == 0x104) && NativeMethods.GetForegroundWindow() == target)
            {
                var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
                if ((key.Flags & 0x10) == 0 && key.Key is 13 or 9) Interlocked.Exchange(ref _signal, 1);
            }
            return NativeMethods.CallNextHookEx(_handle, code, message, data);
        };
        _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Correction commit observation unavailable.");
    });
    public void Stop() => OnUI(() => { if (_handle != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_handle); _handle = IntPtr.Zero; _callback = null; });
    public bool ConsumeCommitSignal() => Interlocked.Exchange(ref _signal, 0) != 0;
    public void Dispose() => Stop();
    private void OnUI(Action action)
    {
        if (dispatcher.HasThreadAccess) { action(); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } }))
            throw new InvalidOperationException("App is closing.");
        completion.Task.GetAwaiter().GetResult();
    }
}
