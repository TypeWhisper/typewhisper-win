using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// One registration per main window, including when that window is hidden in the tray.
internal sealed class WindowsHotkeyRecovery : IDisposable
{
    private const nuint SubclassId = 0x545752;
    private readonly IntPtr _window;
    private readonly NativeMethods.SubclassProc _callback;
    private readonly HotkeyRecoveryState _state = new();
    private readonly Action _interrupt;
    private readonly Func<string?> _recover;
    private readonly Action<string?> _report;
    private bool _disposed;
    private bool _sessionRegistered;
    internal event Action<uint, long>? SessionActivity;
    internal bool SessionNotificationsAvailable => _sessionRegistered;

    internal WindowsHotkeyRecovery(Microsoft.UI.Xaml.Window window, Action interrupt, Func<string?> recover, Action<string?> report)
    {
        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _interrupt = interrupt; _recover = recover; _report = report;
        _callback = ProcessMessage;
        if (!NativeMethods.SetWindowSubclass(_window, _callback, SubclassId, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not watch hotkey resume events.");
        RegisterSession();
    }

    private void RegisterSession()
    {
        if (_sessionRegistered) return;
        _sessionRegistered = WTSRegisterSessionNotification(_window, 0);
        if (!_sessionRegistered) _report(Loc.T("Session notification registration failed. Hotkey recovery after unlocking is unavailable until registration succeeds."));
    }

    private IntPtr ProcessMessage(IntPtr window, uint message, IntPtr reason, IntPtr data, nuint id, IntPtr reference)
    {
        try
        {
            var signal = HotkeyRecoveryState.Classify(message, reason.ToInt64());
            if (!_disposed && signal != HotkeyRecoverySignal.None)
            {
                SessionActivity?.Invoke(message, reason.ToInt64());
                var revision = _state.Invalidate();
                _interrupt();
                if (signal == HotkeyRecoverySignal.Resume) _ = RecoverAsync(revision);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { AppDiagnostics.Write("hotkey.recovery.notification-failed", ex); }
        return NativeMethods.DefSubclassProc(window, message, reason, data);
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
            _report(error ?? (_sessionRegistered ? null : Loc.T("Session notification registration failed. Hotkey recovery after unlocking is unavailable.")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.WriteFailure("hotkey.recovery.failed", ex);
            if (!_disposed) _report(Loc.T("Hotkeys could not be restored. Restart TypeWhisper to retry."));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _state.Dispose();
        if (_sessionRegistered) WTSUnRegisterSessionNotification(_window);
        NativeMethods.RemoveWindowSubclass(_window, _callback, SubclassId);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
