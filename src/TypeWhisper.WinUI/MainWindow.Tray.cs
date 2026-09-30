using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
#if DEBUG
    internal event Action? TrayProbeRequested;
    private static bool TrayProbeEnabled => WinUIProfile.IsTestProfile &&
        Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_TRAY_PROBE") == "1";
    private void ConfigureTrayProbe()
    {
        if (!TrayProbeEnabled) return;
        TestOverlayButton.Content = "Open tray menu";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TestOverlayButton, "Open tray menu");
    }
#endif
    private readonly DictationHotkeyPauseStore _hotkeyPause = new(WinUIProfile.DataPath("dictation-hotkey-pause.json"));
    internal bool DictationHotkeysPaused => _hotkeyPause.Current;
    internal bool CanChangeDictationHotkeyPause => !_closing && !_profileRestoreClosing && _dictationHotkey is not null
        && !(_dictationInput?.IsRecordingOrStarting ?? _dictation.IsRecording);
    internal string? DictationHotkeyPauseError => _hotkeyRecoveryError ?? _hotkeyPause.Error;
    internal event Action? TrayActionsChanged;
    private bool IsNormalLauncherStatus => !_closing && !_profileRestoreClosing && !_pluginsOpen && !_marketplaceOpen
        && string.IsNullOrWhiteSpace(SearchBox.Text) && !(_workflowTask is { IsCompleted: false });
    private string DictationStatusForDisplay => _dictation.FileProcessingStatus is { } fileStatus ? fileStatus : DictationHotkeysPaused && _dictation.OverlayState.Phase == DictationPhase.Idle
        ? "Dictation hotkeys paused. Resume them from the tray menu." : _dictation.Status;

    internal void ToggleDictationHotkeyPause()
    {
        if (!CanChangeDictationHotkeyPause) return;
        if (_hotkeyPause.Save(!_hotkeyPause.Current) is { } error)
        {
            ShowFromActivation(); ShowActivationNotice(error);
        }
        else
        {
            // Keep tracking held keys; resuming must not reinterpret an existing gesture.
            _dictationHotkey?.ObservePause();
            foreach (var entry in _recordingShortcuts.Values) entry.Registration.ObservePause();
            if (IsNormalLauncherStatus) MetricsText.Text = DictationStatusForDisplay;
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
