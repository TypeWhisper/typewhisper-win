using System.Runtime.InteropServices;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// Runs one shortcut test at a time. A separate keyboard hook and mouse handler record what Windows
// delivers; the shortcut registrations report what they react to and run nothing while a test is active.
// Everything runs on the UI thread.
internal static class ShortcutTest
{
    private static readonly NativeMethods.HookProc Callback = Process;
    private static readonly MouseObserver MouseHandler = new();
    private static ShortcutTestSession? _session;
    private static IntPtr _hook;
    private static bool _mouse;

    /// <summary>Whether dictation shortcuts are paused from the tray; set by the main window.</summary>
    internal static Func<bool> Paused { get; set; } = () => false;
    /// <summary>Restarts every shortcut listener and returns an error, or null; set by the main window.</summary>
    internal static Func<string?>? Repair { get; set; }

    internal static bool Active => _session is not null;
    internal static ShortcutTestSession? Session => _session;

    internal static string? Start(IEnumerable<string> expected)
    {
        Stop();
        var hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, Callback, NativeMethods.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            return Loc.T("The shortcut test could not start (Windows error {0}).", Marshal.GetLastWin32Error());
        var chords = expected.ToArray();
        _hook = hook;
        MouseHandler.Queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _mouse = chords.Any(ShortcutKeys.IsMouseChord) && MouseShortcutHook.Add(MouseHandler) is null;
        _session = new(chords, Environment.TickCount64);
        AppDiagnostics.Write("shortcut.test.started");
        return null;
    }

    internal static void Stop()
    {
        if (_hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        if (_mouse) MouseShortcutHook.Remove(MouseHandler);
        _mouse = false;
        _session = null;
    }

    /// <summary>Called by a registration instead of running its action. Returns true while a test is active.</summary>
    internal static bool Respond(string chord)
    {
        if (_session is null) return false;
        _session.Respond(chord);
        return true;
    }

    private static IntPtr Process(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _session is not null)
        {
            var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
            var down = message.ToInt64() is 0x100 or 0x104;
            var up = message.ToInt64() is 0x101 or 0x105;
            // AltGr's synthetic Ctrl and TypeWhisper's own paste keys are not something the user pressed.
            if ((down || up) && !OwnKeyboardInput.Sent(key.Flags, key.Extra) && !ShortcutKeys.IsAltGrControl(key.Key, key.Scan))
                _session.Delivered((int)key.Key, down, Environment.TickCount64);
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    // Watches mouse buttons on the hook thread and hands them to the UI thread; it never owns a click.
    private sealed class MouseObserver : IMouseShortcutHandler
    {
        internal Microsoft.UI.Dispatching.DispatcherQueue? Queue { get; set; }

        public bool Button(int key, bool down, uint time, string modifiers)
        {
            var now = Environment.TickCount64;
            Queue?.TryEnqueue(() => _session?.Delivered(key, down, now));
            return false;
        }

        public void Reset() { }
    }
}
