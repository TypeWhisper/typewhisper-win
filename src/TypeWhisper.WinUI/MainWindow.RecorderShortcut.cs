using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HotkeyRegistration? _recorderHotkey;
    private ProcessingCancelShortcut? _recorderShortcutSettings;

    private void InitializeRecorderShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _recorderHotkey = new(this, OpenRecorderFromShortcut, 0x8800);
        _recorderShortcutSettings = new(WinUIProfile.DataPath("recorder-hotkeys.txt"),
            new RecorderShortcutBackend(_recorderHotkey), ValidateRecorderShortcut, "Recorder shortcuts");
        var error = _recorderShortcutSettings.Initialize();
        _settingsValues["RecorderToggleHotkeys"] = _recorderHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? RecorderShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_recorderHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Recorder. Change that shortcut first." : null;

    private string? ValidateRecorderShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the recorder shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return RecordingShortcutConflict(value) ?? WorkflowPaletteShortcutConflict(value) ?? HistoryShortcutConflict(value) ?? ReadLastShortcutConflict(value) ?? CopyLastShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangeRecorderShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_recorderShortcutSettings is null) return "Recorder shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _recorderShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["RecorderToggleHotkeys"] = _recorderShortcutSettings.Value;
        return error;
    }

    private void OpenRecorderFromShortcut()
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        var busy = _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
            || WorkflowsView.IsBusy || _dictation.Models.Busy || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };
        // Settings has its own window; opening the recorder here does not replace its draft.
        var otherWorkspace = _workflowsOpen || _historyOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen || UtilityOpen;
        if (RecorderShortcutAdmission.Rejection(false, busy, otherWorkspace) is { } refusal)
        { ShowFromActivation(); ShowActivationNotice(refusal); return; }
        ShowFromActivation();
        if (!_recorderOpen) OpenRecorder();
    }

    private sealed class RecorderShortcutBackend(HotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
