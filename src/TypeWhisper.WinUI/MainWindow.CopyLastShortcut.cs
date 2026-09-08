using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private HotkeyRegistration? _copyLastHotkey;
    private ProcessingCancelShortcut? _copyLastShortcutSettings;

    private void InitializeCopyLastShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _copyLastHotkey = new(this, CopyLastTranscription, 0x7C00);
        _copyLastShortcutSettings = new(WinUIProfile.DataPath("copy-last-transcription-hotkeys.txt"),
            new CopyLastShortcutBackend(_copyLastHotkey), ValidateCopyLastShortcut, "Copy last transcription shortcuts");
        var error = _copyLastShortcutSettings.Initialize();
        _settingsValues["CopyLastTranscriptionHotkeys"] = _copyLastHotkey.Value;
        if (error is not null) ShowActivationNotice(error);
    }

    private string? CopyLastShortcutConflict(string value, bool modifierOnly = false) =>
        ProcessingCancelShortcut.Conflicts(_copyLastHotkey?.Value ?? "", WorkflowShortcutCatalog.Canonical(value), modifierOnly)
            ? "Already used by Copy last transcription. Change that shortcut first." : null;

    private string? ValidateCopyLastShortcut(string value)
    {
        if (value != WorkflowShortcutCatalog.Canonical(value)) return "Assign the copy-last shortcut again using the shortcut editor.";
        foreach (var chord in ShortcutRules.Split(value))
            if (ShortcutRules.Validate(chord, false) is { } error) return error;
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_hotkeyRegistration?.Value ?? ""), false))
            return "Already used by Quick Launch.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_dictationHotkey?.Value ?? ""), true))
            return "This shortcut overlaps Main dictation and could start recording. Choose another shortcut.";
        if (ProcessingCancelShortcut.Conflicts(value, WorkflowShortcutCatalog.Canonical(_cancelProcessingHotkey?.Value ?? ""), false))
            return "Already used by Cancel processing.";
        return ReadLastShortcutConflict(value) ?? HistoryShortcutConflict(value) ?? _workflowShortcuts?.Conflict(value);
    }

    private string? ChangeCopyLastShortcut(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_copyLastShortcutSettings is null) return "Copy-last shortcuts are unavailable. Wait for startup to finish or restart.";
        var error = _copyLastShortcutSettings.Save(WorkflowShortcutCatalog.Canonical(value));
        _settingsValues["CopyLastTranscriptionHotkeys"] = _copyLastShortcutSettings.Value;
        return error;
    }

    private void CopyLastTranscription()
    {
        var blocked = _closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing;
        var busy = _dictationInitialization is not { IsCompleted: true } || !_dictation.CanChangeProvider
            || _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false };
        var result = LastDictationCopy.Execute(_dictation.LastCompletedDictation, blocked, busy, text =>
        {
            var content = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
        });
        if (result == LastDictationCopyResult.Ignored) return;
        if (result == LastDictationCopyResult.Busy)
        {
            MetricsText.Text = "Finish the current operation before copying the last dictation.";
            return;
        }
        if (result == LastDictationCopyResult.Copied)
        {
            // A global copy action must not steal focus from the destination app.
            MetricsText.Text = "Last dictation copied";
            return;
        }
        var message = result switch
        {
            LastDictationCopyResult.Busy => "Finish the current operation before copying the last dictation.",
            LastDictationCopyResult.Empty => "No completed dictation in this session yet. Dictate once, then use this shortcut.",
            _ => "Could not access the clipboard. Try copying the last dictation again."
        };
        ShowFromActivation();
        ShowActivationNotice(message);
    }

    private sealed class CopyLastShortcutBackend(HotkeyRegistration registration) : IProcessingCancelShortcutBackend
    {
        public string Value => registration.Value;
        public string? TryChange(string value) => registration.TryChange(value);
    }
}
