using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private readonly DictationHotkeyPauseStore _hotkeyPause = new(WinUIProfile.DataPath("dictation-hotkey-pause.json"));
    internal bool DictationHotkeysPaused => _hotkeyPause.Current;
    internal bool CanChangeDictationHotkeyPause => !_closing && !_profileRestoreClosing && _dictationHotkey is not null
        && !(_dictationInput?.IsRecordingOrStarting ?? _dictation.IsRecording);
    internal string? DictationHotkeyPauseError => _hotkeyRecoveryError ?? _hotkeyPause.Error;
    internal event Action? TrayActionsChanged;

    internal void ToggleDictationHotkeyPause()
    {
        if (!CanChangeDictationHotkeyPause) return;
        if (_hotkeyPause.Save(!_hotkeyPause.Current) is { } error)
        {
            ShowActivationNotice(error);
        }
        else
        {
            // Keep tracking held keys; resuming must not reinterpret an existing gesture.
            _dictationHotkey?.ObservePause();
            foreach (var entry in _recordingShortcuts.Values) entry.Registration.ObservePause();
        }
        TrayActionsChanged?.Invoke();
    }

    internal bool RecorderRecording => RecorderView.IsRecording;
    internal bool CanToggleRecorder => !_closing && !_profileRestoreClosing && RecorderView.CanToggleRecording;
    internal event Action? RecorderChanged
    {
        add => RecorderView.RecordingStateChanged += value;
        remove => RecorderView.RecordingStateChanged -= value;
    }

    internal async void ToggleRecorderFromTray()
    {
        if (!CanToggleRecorder) return;
        // A refused start or failed save is explained on the Recorder page.
        if (!await RecorderView.ToggleRecordingAsync() && !_closing) OpenRecorder(() => RecorderView.FocusEntry());
    }

    // The tray menu took the foreground, so insert into the app used before it opened.
    internal void ShowRecentTranscriptionsFromTray() => OpenRecentTranscriptions(_foregroundHistory?.LastTarget);

    internal void ShowDiagnosticsFromTray()
    {
        if (_closing || _profileRestoreClosing) return;
        OpenSettings();
        _settingsWindow?.ShowSetting("Advanced", DiagnosticsSettingsView.SettingKey);
    }

    internal void OpenRecoveryFromTray()
    {
        if (_closing || _profileRestoreClosing) return;
        var existingSettings = _settingsWindow is not null;
        OpenSettings();
        _settingsWindow?.ShowRecoveryFromTray(allowNavigation: !existingSettings);
    }
}
