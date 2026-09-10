using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TypeWhisper.WinUI;

internal sealed class CorrectionCommitObserver(DispatcherQueue dispatcher, IntPtr target) : ITargetAppCorrectionCommitObserver
{
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    private Hook? _callback;
    private IntPtr _handle;
    private int _signal;
    public void Start() => OnUI(() =>
    {
        Interlocked.Exchange(ref _signal, 0);
        _callback = (code, message, data) =>
        {
            if (code >= 0 && (message == 0x100 || message == 0x104) && GetForegroundWindow() == target)
            {
                var key = Marshal.PtrToStructure<Key>(data);
                if ((key.Flags & 0x10) == 0 && key.Code is 13 or 9) Interlocked.Exchange(ref _signal, 1);
            }
            return CallNextHookEx(_handle, code, message, data);
        };
        _handle = SetWindowsHookExW(13, _callback, GetModuleHandleW(null), 0);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Correction commit observation unavailable.");
    });
    public void Stop() => OnUI(() => { if (_handle != IntPtr.Zero) UnhookWindowsHookEx(_handle); _handle = IntPtr.Zero; _callback = null; });
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
    [StructLayout(LayoutKind.Sequential)] private struct Key { public uint Code, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int hook, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
