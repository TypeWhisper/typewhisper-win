using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HotkeyRegistration? _pasteLastHotkey;
    private ProcessingCancelShortcut? _pasteLastShortcutSettings;
    private ForegroundWindowHistory? _foregroundHistory;

    private void InitializePasteLastShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _foregroundHistory = new();
        // The shortcut pastes into the app window it was pressed in.
        _pasteLastHotkey = new(this, () => _ = PasteLastTranscriptionAsync(ForegroundWindowHistory.CurrentTarget, activate: false), 0x8A00);
        _pasteLastShortcutSettings = new(WinUIProfile.DataPath("paste-last-transcription-hotkeys.txt"),
            new PasteLastShortcutBackend(_pasteLastHotkey), ValidatePasteLastShortcut, "Paste last transcription shortcuts");
        var error = _pasteLastShortcutSettings.Initialize();
        _settingsValues["PasteLastTranscriptionHotkeys"] = _pasteLastHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? PasteLastShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_pasteLastHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Paste last transcription. Change that shortcut first." : null;

    private string? ValidatePasteLastShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the paste-last shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return RecordingShortcutConflict(value) ?? RecorderShortcutConflict(value) ?? WorkflowPaletteShortcutConflict(value) ?? ReadLastShortcutConflict(value)
            ?? HistoryShortcutConflict(value) ?? CopyLastShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangePasteLastShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_pasteLastShortcutSettings is null) return "Paste-last shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _pasteLastShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["PasteLastTranscriptionHotkeys"] = _pasteLastShortcutSettings.Value;
        return error;
    }

    // The tray menu takes the foreground, so it pastes into the last app window used before.
    internal void PasteLastTranscriptionFromTray() => _ = PasteLastTranscriptionAsync(_foregroundHistory?.LastTarget, activate: true);
    internal void CopyLastTranscriptionFromTray() => CopyLastTranscription();
    internal void ReadLastTranscriptionFromTray() => ReadLastTranscription();

    private async Task PasteLastTranscriptionAsync(PasteTarget? target, bool activate)
    {
        var blocked = _closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing;
        var busy = _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
            || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };
        LastDictationPasteResult result;
        try { result = await _dictation.PasteLastCompletedAsync(target, activate, blocked, busy); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Paste last transcription failed: {0}", ex);
            result = LastDictationPasteResult.NotPasted;
        }
        if (_closing || result == LastDictationPasteResult.Ignored) return;
        if (result == LastDictationPasteResult.Pasted)
        {
            // A global paste action must not steal focus from the destination app.
            MetricsText.Text = "Last dictation pasted";
            return;
        }
        var message = result switch
        {
            LastDictationPasteResult.Busy => "Finish the current operation before pasting the last dictation.",
            LastDictationPasteResult.Empty => "No completed dictation in this session yet. Dictate once, then use this shortcut.",
            LastDictationPasteResult.NoTarget => "Click into the app you want to paste into, then try again.",
            _ => "Could not paste the last dictation. Release all keys, click into a text field and try again, or use Copy last transcription."
        };
        ShowFromActivation();
        ShowActivationNotice(message);
    }

    private sealed class PasteLastShortcutBackend(HotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
