namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private WindowsHotkeyRecovery? _hotkeyRecovery;
    private string? _hotkeyRecoveryError;

    private void InitializeHotkeyRecovery()
    {
        try
        {
            _hotkeyRecovery = new(this, () =>
            {
                _dictationInput?.InterruptPendingGesture();
                _dictationHotkey?.Interrupt();
                foreach (var entry in _recordingShortcuts.Values) entry.Registration.Interrupt();
            }, () =>
            {
                if (_closing || _profileRestoreClosing) return null;
                var errors = new List<string>();
                if (_dictationHotkey?.Recover() is { } error) errors.Add(error);
                foreach (var entry in _recordingShortcuts.Values)
                    if (entry.Registration.Recover() is { } failure) errors.Add(failure);
                return errors.Count == 0 ? null : string.Join(" ", errors.Distinct());
            }, ReportHotkeyRecovery);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Hotkey resume monitoring failed: {0}", ex);
            ReportHotkeyRecovery("Hotkey recovery after sleep could not start. Restart TypeWhisper if dictation shortcuts stop responding.");
        }
    }

    private void ReportHotkeyRecovery(string? error)
    {
        if (_closing || _profileRestoreClosing) return;
        _hotkeyRecoveryError = error;
        if (error is not null) System.Diagnostics.Trace.TraceError(error);
        if (IsNormalLauncherStatus) MetricsText.Text = error ?? DictationStatusForDisplay;
        TrayActionsChanged?.Invoke();
    }
}
