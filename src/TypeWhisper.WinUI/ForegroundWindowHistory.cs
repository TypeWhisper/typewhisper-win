using System.Runtime.InteropServices;
using System.Text;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// A paste destination. The process ID detects a closed window whose handle Windows reused for another app.
internal readonly record struct PasteTarget(IntPtr Window, uint ProcessId)
{
    internal bool IsCurrent => Window != IntPtr.Zero && ForegroundWindowHistory.ProcessOf(Window) == ProcessId;
}

// Remembers the last app window the user worked in, so tray actions can paste there
// after the tray menu itself took the foreground.
internal sealed class ForegroundWindowHistory : IDisposable
{
    private const uint ForegroundEvent = 3;
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private readonly WinEventProc _callback;
    private IntPtr _hook;
    private PasteTarget? _last;

    internal ForegroundWindowHistory()
    {
        _callback = (_, _, window, _, _, _, _) => _last = Eligible(window) ?? _last;
        // Out-of-context events are delivered on this (UI) thread through its message loop.
        _hook = SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, _callback, 0, 0, 0);
        _last = Eligible(GetForegroundWindow());
    }

    internal PasteTarget? LastTarget => _last is { IsCurrent: true } last && IsWindowVisible(last.Window) ? last : null;

    // The shortcut pastes where it is pressed, but never into TypeWhisper or shell windows.
    internal static PasteTarget? CurrentTarget => Eligible(GetForegroundWindow());

    internal static uint ProcessOf(IntPtr window) =>
        IsWindow(window) && GetWindowThreadProcessId(window, out var processId) != 0 ? processId : 0;

    private static PasteTarget? Eligible(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var processId = ProcessOf(window);
        var name = new StringBuilder(256);
        var length = GetClassName(window, name, name.Capacity);
        return PasteTargetFilter.IsEligible(length > 0 ? name.ToString() : null, processId, OwnProcessId) ? new(window, processId) : null;
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
