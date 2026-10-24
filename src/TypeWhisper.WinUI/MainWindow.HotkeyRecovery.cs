namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private WindowsHotkeyRecovery? _hotkeyRecovery;
    private string? _hotkeyRecoveryError;

    private void InitializeHotkeyRecovery()
    {
        ShortcutTest.Paused = () => DictationHotkeysPaused;
        ShortcutTest.Repair = RepairHotkeys;
        try
        {
            _hotkeyRecovery = new(this, InterruptHotkeys, RecoverHotkeys, ReportHotkeyRecovery);
            _hotkeyRecovery.SessionActivity += (message, reason) =>
                _dictation?.ObservePrerollSession(message, reason, _hotkeyRecovery.SessionNotificationsAvailable);
            _dictation?.ObservePrerollSession(0, 0, _hotkeyRecovery.SessionNotificationsAvailable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dictation?.ObservePrerollSession(0, 0, false);
            AppDiagnostics.Write("hotkey.recovery.start-failed", ex);
            ReportHotkeyRecovery(Loc.T("Hotkey recovery after sleep could not start. Restart TypeWhisper if dictation shortcuts stop responding."));
        }
    }

    private void InterruptHotkeys()
    {
        _dictationInput?.InterruptPendingGesture();
        _dictationHotkey?.Interrupt();
        foreach (var entry in _recordingShortcuts.Values) entry.Registration.Interrupt();
        _dictationKeyHook?.Interrupt();
        MouseShortcutHook.Interrupt();
    }

    private string? RecoverHotkeys()
    {
        if (_closing || _profileRestoreClosing) return null;
        // Unlock may arrive before Windows switches back to the interactive desktop.
        // Re-evaluate here without clearing any lock, sleep or disconnect reason.
        _dictation?.ObservePrerollSession(0, 0, _hotkeyRecovery?.SessionNotificationsAvailable == true);
        // A prepared microphone client can go stale across sleep or a session switch.
        _dictation?.RefreshMicrophoneAfterResume();
        // Registry notifications can be lost across sleep or a session switch.
        WinUIManagedPolicy.RequestRefresh();
        var errors = new List<string>();
        if (_dictationHotkey?.Recover() is { } error) errors.Add(error);
        foreach (var entry in _recordingShortcuts.Values)
            if (entry.Registration.Recover() is { } failure) errors.Add(failure);
        if (_dictationKeyHook?.Recover() is { } keyError) errors.Add(keyError);
        if (MouseShortcutHook.Recover() is { } mouseError) errors.Add(mouseError);
        return errors.Count == 0 ? null : string.Join(" ", errors.Distinct());
    }

    // The shortcut test's Repair: the same restart as after sleep, on request.
    private string? RepairHotkeys()
    {
        if (_closing || _profileRestoreClosing) return Loc.T("The app is shutting down.");
        AppDiagnostics.Write("shortcut.test.repair");
        InterruptHotkeys();
        var error = RecoverHotkeys();
        ReportHotkeyRecovery(error);
        return error;
    }

    private void ReportHotkeyRecovery(string? error)
    {
        if (_closing || _profileRestoreClosing) return;
        _hotkeyRecoveryError = error;
        if (error is not null) AppDiagnostics.Write("hotkey.recovery.reported");
        if (error is not null) ShowNotice(new AppNotice(error));
        TrayActionsChanged?.Invoke();
    }
}
