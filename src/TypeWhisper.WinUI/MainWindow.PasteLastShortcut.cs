using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private ForegroundWindowHistory? _foregroundHistory;

    private void InitializePasteLastShortcut()
    {
        if (_closing || _profileRestoreClosing) return;
        _foregroundHistory = new();
        // The shortcut pastes into the app window it was pressed in.
        _pasteLastShortcut = InitializeActionShortcut(GlobalShortcuts.PasteLastTranscription,
            () => _ = PasteLastTranscriptionAsync(ForegroundWindowHistory.CurrentTarget, activate: false), 0x8A00,
            "paste-last-transcription-hotkeys.txt", "Paste last transcription shortcuts", "paste-last");
    }

    private string? ChangePasteLastShortcut(string value) => ChangeActionShortcut(_pasteLastShortcut, "Paste-last", value);

    // The tray menu takes the foreground, so it pastes into the last app window used before.
    internal void PasteLastTranscriptionFromTray() => _ = PasteLastTranscriptionAsync(_foregroundHistory?.LastTarget, activate: true);
    internal void CopyLastTranscriptionFromTray() => CopyLastTranscription();
    internal void ReadLastTranscriptionFromTray() => ReadLastTranscription();

    private async Task PasteLastTranscriptionAsync(PasteTarget? target, bool activate)
    {
        var blocked = _closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing;
        var busy = ShortcutActionBusy;
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
            return;
        }
        var message = result switch
        {
            LastDictationPasteResult.Busy => "Finish the current operation before pasting the last dictation.",
            LastDictationPasteResult.Empty => "No completed dictation in this session yet. Dictate once, then use this shortcut.",
            LastDictationPasteResult.NoTarget => "Click into the app you want to paste into, then try again.",
            _ => "Could not paste the last dictation. Release all keys, click into a text field and try again, or use Copy last transcription."
        };
        ShowActivationNotice(message);
    }
}
