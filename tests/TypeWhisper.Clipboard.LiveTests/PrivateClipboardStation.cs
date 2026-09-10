using System.ComponentModel;
using System.Runtime.InteropServices;

// Process-local isolation: the worker never reads or replaces the interactive clipboard.
internal sealed class PrivateClipboardStation : IDisposable
{
    private readonly IntPtr _previousStation = GetProcessWindowStation();
    private readonly IntPtr _previousDesktop = GetThreadDesktop(GetCurrentThreadId());
    private IntPtr _station;
    private IntPtr _desktop;
    internal PrivateClipboardStation()
    {
        _station = CreateWindowStation("TypeWhisperClipboardTests-" + Guid.NewGuid().ToString("N"), 0, 0x37F, IntPtr.Zero);
        if (_station == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetProcessWindowStation(_station)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _desktop = CreateDesktop("ClipboardTests", null, IntPtr.Zero, 0, 0x1FF, IntPtr.Zero);
            if (_desktop == IntPtr.Zero || !SetThreadDesktop(_desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (GetProcessWindowStation() != _station) throw new InvalidOperationException("Clipboard isolation failed.");
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        SetThreadDesktop(_previousDesktop);
        SetProcessWindowStation(_previousStation);
        if (_desktop != IntPtr.Zero) { CloseDesktop(_desktop); _desktop = IntPtr.Zero; }
        if (_station != IntPtr.Zero) { CloseWindowStation(_station); _station = IntPtr.Zero; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, string? device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
