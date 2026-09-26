using System.Runtime.InteropServices;
using System.Text;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Remembers the last app window the user worked in, so tray actions can paste there
// after the tray menu itself took the foreground.
internal sealed class ForegroundWindowHistory : IDisposable
{
    private const uint ForegroundEvent = 3;
    private readonly WinEventProc _callback;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private IntPtr _hook;
    private IntPtr _last;
    private uint _lastProcessId;

    internal ForegroundWindowHistory()
    {
        _callback = (_, _, window, _, _, _, _) => Observe(window);
        // Out-of-context events are delivered on this (UI) thread through its message loop.
        _hook = SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, _callback, 0, 0, 0);
        Observe(GetForegroundWindow());
    }

    // The process check rejects a closed window whose handle Windows reused for another app.
    internal IntPtr LastTarget => _last != IntPtr.Zero && IsWindow(_last) && IsWindowVisible(_last)
        && GetWindowThreadProcessId(_last, out var processId) != 0 && processId == _lastProcessId ? _last : IntPtr.Zero;

    private void Observe(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var processId);
        var name = new StringBuilder(256);
        var length = GetClassName(window, name, name.Capacity);
        if (PasteTargetFilter.IsEligible(length > 0 ? name.ToString() : null, processId, _ownProcessId))
        {
            _last = window;
            _lastProcessId = processId;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
}
