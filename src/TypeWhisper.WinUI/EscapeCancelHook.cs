using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// Owns bare Escape only while a dictation can be cancelled. Every other key, and Escape
// while idle, continues to the focused app unchanged.
internal sealed class EscapeCancelHook : IDisposable
{
    private const int Escape = 0x1B;
    private readonly NativeMethods.HookProc _callback;
    private readonly EscapeKeyFilter _filter = new();
    private IntPtr _hook;
    private bool _interrupted;
    private bool _disposed;

    internal EscapeCancelHook(Func<bool> available, Action pressed)
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && !_interrupted && !_disposed)
            {
                var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
                var down = message.ToInt64() is 0x100 or 0x104;
                var up = message.ToInt64() is 0x101 or 0x105;
                if (key.Key == Escape && (key.Flags & 0x10) == 0 && (down || up))
                {
                    // The hook runs on the installing UI thread and must return quickly;
                    // callers only record intent here and schedule the cancellation.
                    var (consume, press) = _filter.Key(down, key.Time,
                        down && !ShortcutRecorder.AnyEditing && available(), down && ModifiersHeld());
                    if (press) pressed();
                    if (consume) return (IntPtr)1;
                }
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void Interrupt()
    {
        _interrupted = true;
        _filter.Reset();
    }

    internal string? Recover()
    {
        if (_disposed) return null;
        var replacement = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero)
            return Loc.T("Could not restore the Escape cancel hook (Windows error {0}). Restart TypeWhisper if Esc stops cancelling dictation.", Marshal.GetLastWin32Error());
        var previous = _hook;
        _hook = replacement;
        if (previous != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(previous);
        _filter.Reset();
        _interrupted = false;
        return null;
    }

    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);

    public void Dispose() { if (_disposed) return; _disposed = true; NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
}
