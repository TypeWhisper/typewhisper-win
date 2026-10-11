using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// Owns bare Escape only while a dictation can be cancelled, and bare Enter (main or keypad) only while
// it submits the recording. Every other key, and both keys otherwise, continue to the focused app unchanged.
// Modifiers of a held dictation shortcut do not count for Enter, so push-to-talk can submit too.
internal sealed class DictationKeyHook : IDisposable
{
    private const int Escape = 0x1B;
    private const int Enter = 0x0D;
    private readonly NativeMethods.HookProc _callback;
    private readonly EscapeKeyFilter _escape = new(), _enter = new();
    private IntPtr _hook;
    private bool _interrupted;
    private bool _disposed;

    internal DictationKeyHook(Func<bool> escapeAvailable, Action escapePressed, Func<bool> enterAvailable, Action enterPressed,
        Func<int, bool> heldByShortcut, Action enterWithModifiers)
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && !_interrupted && !_disposed)
            {
                var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
                var down = message.ToInt64() is 0x100 or 0x104;
                var up = message.ToInt64() is 0x101 or 0x105;
                var filter = key.Key == Escape ? _escape : key.Key == Enter ? _enter : null;
                // Generated input, including the Enter that submits a dictation, is never owned.
                if (filter is not null && (key.Flags & 0x10) == 0 && (down || up))
                {
                    // The hook runs on the installing UI thread and must return quickly;
                    // callers only record intent here and schedule the work.
                    var escape = filter == _escape;
                    var available = down && !ShortcutRecorder.AnyEditing && (escape ? escapeAvailable() : enterAvailable());
                    var modifiers = down && ModifiersHeld(escape ? null : heldByShortcut);
                    var (consume, press) = filter.Key(down, key.Time, available, modifiers);
                    if (press) (escape ? escapePressed : enterPressed)();
                    else if (!escape && !consume && available && modifiers) enterWithModifiers();
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
        AppDiagnostics.Write("shortcut.key-hook.interrupted");
        _escape.Reset();
        _enter.Reset();
    }

    internal string? Recover()
    {
        if (_disposed) return null;
        var replacement = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero)
            return Loc.T("Could not restore the Esc and Enter keyboard hook (Windows error {0}). Restart TypeWhisper if Esc stops cancelling dictation or Enter stops submitting it.", Marshal.GetLastWin32Error());
        var previous = _hook;
        _hook = replacement;
        if (previous != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(previous);
        _escape.Reset();
        _enter.Reset();
        _interrupted = false;
        AppDiagnostics.Write("shortcut.key-hook.recovered");
        return null;
    }

    // Escape checks the generic keys; Enter checks left and right keys, so a held shortcut can exempt its own.
    private static readonly int[] GenericModifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private static readonly int[] SideModifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
    private static bool ModifiersHeld(Func<int, bool>? exempt) => (exempt is null ? GenericModifiers : SideModifiers)
        .Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0 && exempt?.Invoke(key) != true);

    public void Dispose() { if (_disposed) return; _disposed = true; NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
}
