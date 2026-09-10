using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI;

// One registration per main window, including when that window is hidden in the tray.
internal sealed class WindowsHotkeyRecovery : IDisposable
{
    private const nuint SubclassId = 0x545752;
    private readonly IntPtr _window;
    private readonly SubclassProc _callback;
    private readonly HotkeyRecoveryState _state = new();
    private readonly Action _interrupt;
    private readonly Func<string?> _recover;
    private readonly Action<string?> _report;
    private bool _disposed;
    private bool _sessionRegistered;

    internal WindowsHotkeyRecovery(Microsoft.UI.Xaml.Window window, Action interrupt, Func<string?> recover, Action<string?> report)
    {
        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _interrupt = interrupt; _recover = recover; _report = report;
        _callback = ProcessMessage;
        if (!SetWindowSubclass(_window, _callback, SubclassId, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not watch hotkey resume events.");
        RegisterSession();
    }

    private void RegisterSession()
    {
        if (_sessionRegistered) return;
        _sessionRegistered = WTSRegisterSessionNotification(_window, 0);
        if (!_sessionRegistered) _report("Session notification registration failed. Hotkey recovery after unlocking is unavailable until registration succeeds.");
    }

    private IntPtr ProcessMessage(IntPtr window, uint message, IntPtr reason, IntPtr data, nuint id, IntPtr reference)
    {
        try
        {
            var signal = HotkeyRecoveryState.Classify(message, reason.ToInt64());
            if (!_disposed && signal != HotkeyRecoverySignal.None)
            {
                var revision = _state.Invalidate();
                _interrupt();
                if (signal == HotkeyRecoverySignal.Resume) _ = RecoverAsync(revision);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Hotkey recovery notification failed: {0}", ex); }
        return DefSubclassProc(window, message, reason, data);
    }

    private async Task RecoverAsync(int revision)
    {
        try
        {
            // Resume and unlock often arrive together. Recover once after they settle.
            await Task.Delay(500);
            if (!_state.IsCurrent(revision)) return;
            RegisterSession();
            var error = _recover();
            if (error is not null)
            {
                _report(error);
                await Task.Delay(1500);
                if (!_state.IsCurrent(revision)) return;
                error = _recover();
            }
            _report(error ?? (_sessionRegistered ? null : "Session notification registration failed. Hotkey recovery after unlocking is unavailable."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Hotkey recovery failed: {0}", ex);
            if (!_disposed) _report("Hotkeys could not be restored. Restart TypeWhisper to retry.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _state.Dispose();
        if (_sessionRegistered) WTSUnRegisterSessionNotification(_window);
        RemoveWindowSubclass(_window, _callback, SubclassId);
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr reason, IntPtr data, nuint id, IntPtr reference);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, IntPtr reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr reason, IntPtr data);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
