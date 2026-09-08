using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private PrototypeHotkeyRegistration? _historyHotkey;
    private ProcessingCancelShortcut? _historyShortcutSettings;

    private void InitializeHistoryShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _historyHotkey = new(this, OpenHistoryFromShortcut, 0x7A00);
        _historyShortcutSettings = new(WinUIProfile.DataPath("recent-transcriptions-hotkeys.txt"),
            new HistoryShortcutBackend(_historyHotkey), ValidateHistoryShortcut, "Recent transcription shortcuts");
        var error = _historyShortcutSettings.Initialize();
        _settingsValues["RecentTranscriptionsHotkeys"] = _historyHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? HistoryShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_historyHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Recent transcriptions. Change that shortcut first." : null;

    private string? ValidateHistoryShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the History shortcut again using the shortcut editor.";
        foreach (var chord in PrototypeShortcutRules.Split(value))
            if (PrototypeShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return CopyLastShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangeHistoryShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_historyShortcutSettings is null) return "History shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _historyShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["RecentTranscriptionsHotkeys"] = _historyShortcutSettings.Value;
        return error;
    }

    private void OpenHistoryFromShortcut()
    {
        if (_closing || _profileRestoreClosing || PrototypeShortcutRecorder.AnyEditing) return;
        var busy = _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
            || _dictation.Models.Busy || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };
        // Settings has its own window; opening History here does not replace its draft.
        var otherWorkspace = _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen;
        if (HistoryShortcutAdmission.Rejection(false, busy, otherWorkspace) is { } refusal)
        { ShowFromActivation(); ShowActivationNotice(refusal); return; }
        ShowFromActivation();
        if (!_historyOpen) OpenHistory();
    }

    private sealed class HistoryShortcutBackend(PrototypeHotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
