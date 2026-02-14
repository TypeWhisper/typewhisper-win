using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

public sealed class ActiveWindowService : IActiveWindowService
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    public string? GetActiveWindowProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == OwnProcessId) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetActiveWindowTitle()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0) return null;

        var buffer = new char[length + 1];
        var result = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Length);
        return result > 0 ? new string(buffer, 0, result) : null;
    }

    public string? GetBrowserUrl()
    {
        // UI Automation is expensive, defer to Phase 3 profiles
        // For now return null; will be implemented with
        // System.Windows.Automation when profile matching needs it
        return null;
    }
}
