using System.Runtime.InteropServices;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// Receives presses and releases of the middle and side mouse buttons.
internal interface IMouseShortcutHandler
{
    /// <summary>Runs on the mouse hook thread and must return at once. Returns whether the event belongs to a
    /// shortcut and must not reach other apps; any other work goes to the UI thread.</summary>
    /// <param name="key">The button as a virtual-key code: 0x04, 0x05 or 0x06.</param>
    /// <param name="down">True for a press, false for a release.</param>
    /// <param name="time">The event time on the Windows uptime clock.</param>
    /// <param name="modifiers">The modifiers held during the event, e.g. "CTRL+SHIFT".</param>
    bool Button(int key, bool down, uint time, string modifiers);

    /// <summary>Forgets presses whose release may have been lost, e.g. while the hook was interrupted.</summary>
    void Reset();
}

// The one low-level mouse hook for shortcuts on the middle or a side mouse button. It is installed only
// while such a shortcut exists, so Windows does not pass every mouse move through TypeWhisper otherwise.
// It runs on a thread of its own: Windows skips, and eventually removes, a low-level hook whose thread is
// busy, and a side button's release would then reach the focused app as "back".
internal static class MouseShortcutHook
{
    private static readonly object Gate = new();
    private static IMouseShortcutHandler[] _handlers = [];
    private static HookThread? _thread;
    private static bool _interrupted;
    // Only the current hook thread handles events, even if an old one has not shut down yet.
    private static int _activeThread;

    // Add, Remove, Interrupt and Recover are called on the UI thread.
    internal static string? Add(IMouseShortcutHandler handler)
    {
        lock (Gate)
        {
            if (_handlers.Contains(handler)) return null;
            // Also after a failed recovery, so a newly saved mouse shortcut never stays silent.
            if ((_thread is null || Volatile.Read(ref _interrupted)) && Restart() is { } error) return error;
            Volatile.Write(ref _handlers, _handlers.Append(handler).ToArray());
            return null;
        }
    }

    internal static void Remove(IMouseShortcutHandler handler)
    {
        lock (Gate)
        {
            if (!_handlers.Contains(handler)) return;
            Volatile.Write(ref _handlers, _handlers.Where(item => !ReferenceEquals(item, handler)).ToArray());
            if (_handlers.Length > 0) return;
            _thread?.Dispose();
            _thread = null;
            Volatile.Write(ref _activeThread, 0);
            Volatile.Write(ref _interrupted, false);
        }
    }

    internal static void Interrupt()
    {
        lock (Gate)
        {
            if (_thread is null) return;
            Volatile.Write(ref _interrupted, true);
            foreach (var handler in _handlers) handler.Reset();
        }
        AppDiagnostics.Write("shortcut.mouse-hook.interrupted");
    }

    // Windows can drop a low-level hook across sleep or a session switch, so it is installed again.
    internal static string? Recover()
    {
        lock (Gate)
        {
            if (_handlers.Length == 0) return null;
            if (Restart() is { } error) return error;
            foreach (var handler in _handlers) handler.Reset();
        }
        AppDiagnostics.Write("shortcut.mouse-hook.recovered");
        return null;
    }

    // Called with Gate held. The old hook goes first, so no event reaches the handlers twice.
    private static string? Restart()
    {
        _thread?.Dispose();
        _thread = HookThread.Start(out var error);
        Volatile.Write(ref _activeThread, _thread?.ManagedThreadId ?? 0);
        if (_thread is null)
            return Loc.T("Mouse button shortcuts are unavailable (Windows error {0}). Restart TypeWhisper to retry.", error);
        Volatile.Write(ref _interrupted, false);
        return null;
    }

    // The modifiers held right now, in the order ShortcutRules.Normalize uses.
    private static string Modifiers() => string.Join("+", new[]
    {
        Down(0x11) ? "CTRL" : "", Down(0x12) ? "ALT" : "", Down(0x10) ? "SHIFT" : "", Down(0x5B) || Down(0x5C) ? "WIN" : ""
    }.Where(part => part.Length > 0));

    private static bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    // Runs on the hook thread.
    private static IntPtr Process(int code, IntPtr message, IntPtr data)
    {
        // Most events are mouse moves; only button messages read the event data.
        if (code >= 0 && !Volatile.Read(ref _interrupted) && Volatile.Read(ref _activeThread) == Environment.CurrentManagedThreadId
            && MouseShortcutPresses.IsButtonMessage(message.ToInt64()))
        {
            var handlers = Volatile.Read(ref _handlers);
            var mouse = handlers.Length > 0 ? Marshal.PtrToStructure<NativeMethods.MouseHookData>(data) : default;
            if (handlers.Length > 0 && MouseShortcutPresses.TryDecode(message.ToInt64(), mouse.MouseData, out var key, out var down))
            {
                var owned = false;
                try
                {
                    var modifiers = Modifiers();
                    foreach (var handler in handlers) owned |= handler.Button(key, down, mouse.Time, modifiers);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppDiagnostics.Write("shortcut.mouse-hook.failed", ex);
                }
                if (owned) return (IntPtr)1;
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    // A background thread that installs the hook and pumps messages, which is when Windows calls the hook.
    private sealed class HookThread : IDisposable
    {
        private static readonly NativeMethods.HookProc Callback = Process;
        private readonly Thread _thread;
        private uint _threadId;
        private int _error;
        private bool _installed;

        private HookThread()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = NativeMethods.GetCurrentThreadId();
                // Creates the thread's message queue, so Dispose can post WM_QUIT to it.
                NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
                var hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, Callback, NativeMethods.GetModuleHandle(null), 0);
                _error = hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                _installed = hook != IntPtr.Zero;
                ready.Set();
                if (!_installed) return;
                while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                NativeMethods.UnhookWindowsHookEx(hook);
            })
            {
                IsBackground = true,
                Name = "TypeWhisper mouse shortcuts",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            ready.Wait();
        }

        internal int ManagedThreadId => _thread.ManagedThreadId;

        internal static HookThread? Start(out int error)
        {
            var thread = new HookThread();
            error = thread._error;
            return thread._installed ? thread : null;
        }

        public void Dispose()
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (!_thread.Join(TimeSpan.FromSeconds(2))) AppDiagnostics.Write("shortcut.mouse-hook.stop-timeout");
        }
    }
}
