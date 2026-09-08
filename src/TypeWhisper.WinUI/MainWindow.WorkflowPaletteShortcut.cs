using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HotkeyRegistration? _workflowPaletteHotkey;
    private ProcessingCancelShortcut? _workflowPaletteShortcutSettings;

    private void InitializeWorkflowPaletteShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _workflowPaletteHotkey = new(this, OpenWorkflowPaletteFromShortcut, 0x8000);
        _workflowPaletteShortcutSettings = new(WinUIProfile.DataPath("workflow-palette-hotkeys.txt"),
            new WorkflowPaletteShortcutBackend(_workflowPaletteHotkey), ValidateWorkflowPaletteShortcut, "Workflow palette shortcuts");
        var error = _workflowPaletteShortcutSettings.Initialize();
        _settingsValues["WorkflowPaletteHotkeys"] = _workflowPaletteHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? WorkflowPaletteShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_workflowPaletteHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Workflow palette. Change that shortcut first." : null;

    private string? ValidateWorkflowPaletteShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the workflow palette shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return RecordingShortcutConflict(value) ?? RecorderShortcutConflict(value) ?? HistoryShortcutConflict(value) ?? ReadLastShortcutConflict(value) ?? CopyLastShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangeWorkflowPaletteShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_workflowPaletteShortcutSettings is null) return "Workflow palette shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _workflowPaletteShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["WorkflowPaletteHotkeys"] = _workflowPaletteShortcutSettings.Value;
        return error;
    }

    private void OpenWorkflowPaletteFromShortcut()
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        var busy = _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
            || WorkflowsView.IsBusy || _dictation.Models.Busy || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };
        // Settings has its own window; opening the workflow palette here does not replace its draft.
        var otherWorkspace = _historyOpen || _recorderOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen || UtilityOpen;
        if (WorkflowPaletteShortcutAdmission.Rejection(false, busy, otherWorkspace) is { } refusal)
        { ShowFromActivation(); ShowActivationNotice(refusal); return; }
        ShowFromActivation();
        if (!_workflowsOpen) OpenWorkflows();
        else if (WorkflowsView.IsDetail) WorkflowsView.FocusEntry();
        else SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private sealed class WorkflowPaletteShortcutBackend(HotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
