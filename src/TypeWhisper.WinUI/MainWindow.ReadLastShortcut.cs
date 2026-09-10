using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HotkeyRegistration? _readLastHotkey;
    private ProcessingCancelShortcut? _readLastShortcutSettings;

    private void InitializeReadLastShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _readLastHotkey = new(this, ReadLastTranscription, 0x7E00);
        _readLastShortcutSettings = new(WinUIProfile.DataPath("read-last-transcription-hotkeys.txt"),
            new ReadLastShortcutBackend(_readLastHotkey), ValidateReadLastShortcut, "Read last transcription shortcuts");
        var error = _readLastShortcutSettings.Initialize();
        _settingsValues["ReadLastTranscriptionHotkeys"] = _readLastHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? ReadLastShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_readLastHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Read last transcription. Change that shortcut first." : null;

    private string? ValidateReadLastShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the read-last shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return RecordingShortcutConflict(value) ?? RecorderShortcutConflict(value) ?? WorkflowPaletteShortcutConflict(value) ?? CopyLastShortcutConflict(value) ?? HistoryShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangeReadLastShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_readLastShortcutSettings is null) return "Read-last shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _readLastShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["ReadLastTranscriptionHotkeys"] = _readLastShortcutSettings.Value;
        return error;
    }

    private long _readLastRevision;
    private async void ReadLastTranscription()
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        if (_dictationInitialization is not { IsCompleted: true } ||
            (!_dictation.SpokenFeedback.IsBusy && (!_dictation.CanChangeProvider || _dictation.Models.Busy)) ||
            _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false })
        {
            MetricsText.Text = "Finish the current operation before reading the last dictation.";
            return;
        }
        var revision = ++_readLastRevision;
        var result = await _dictation.ToggleReadLastDictationAsync();
        if (_closing || _profileRestoreClosing || revision != _readLastRevision) return;
        MetricsText.Text = result.Message ?? (result.Status == SpokenFeedbackStatus.Completed ? "Finished reading last dictation" : "Read-back stopped");
        if (result.Status is SpokenFeedbackStatus.Failed or SpokenFeedbackStatus.Rejected)
        {
            ShowFromActivation();
            ShowActivationNotice(result.Message ?? "The last dictation could not be read aloud.");
        }
    }

    private sealed class ReadLastShortcutBackend(HotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
