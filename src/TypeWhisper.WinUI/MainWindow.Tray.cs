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
    internal string? DictationHotkeyPauseError => _hotkeyPause.Error;
    internal event Action? TrayActionsChanged;
    private bool IsNormalLauncherStatus => !_closing && !_profileRestoreClosing && !_historyOpen && !_recorderOpen
        && !_workflowsOpen && !_pluginsOpen && !_marketplaceOpen && !LexiconOpen && !FileTranscriptionOpen
        && string.IsNullOrWhiteSpace(SearchBox.Text) && !(_workflowTask is { IsCompleted: false });
    private string DictationStatusForDisplay => DictationHotkeysPaused && _dictation.OverlayState.Phase == DictationPhase.Idle
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
            if (IsNormalLauncherStatus) MetricsText.Text = DictationStatusForDisplay;
        }
        TrayActionsChanged?.Invoke();
    }

    internal void OpenRecoveryFromTray()
    {
        if (_closing || _profileRestoreClosing) return;
        var existingSettings = _settingsWindow is not null;
        OpenSettings();
        _settingsWindow?.ShowRecoveryFromTray(allowNavigation: !existingSettings);
    }
}
