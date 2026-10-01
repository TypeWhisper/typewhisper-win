using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private void InitializeCopyLastShortcut() => _copyLastShortcut = InitializeActionShortcut(GlobalShortcuts.CopyLastTranscription,
        () => CopyLastTranscription(), 0x7C00, "copy-last-transcription-hotkeys.txt", Loc.T("Copy last transcription shortcuts"), Loc.T("copy-last"));

    private string? ChangeCopyLastShortcut(string value) => ChangeActionShortcut(_copyLastShortcut, Loc.T("Copy-last"), value);

    private void CopyLastTranscription()
    {
        var blocked = _closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing;
        var result = LastDictationCopy.Execute(_dictation.LastCompletedDictation, blocked, ShortcutActionBusy, text =>
        {
            var content = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
        });
        if (result == LastDictationCopyResult.Ignored) return;
        if (result == LastDictationCopyResult.Copied)
        {
            // A global copy action must not steal focus from the destination app.
            ShowNotice(new AppNotice(Loc.T("Last dictation copied."), IsError: false, Duration: TimeSpan.FromSeconds(3)));
            return;
        }
        var message = result switch
        {
            LastDictationCopyResult.Busy => Loc.T("Finish the current operation before copying the last dictation."),
            LastDictationCopyResult.Empty => Loc.T("No completed dictation in this session yet. Dictate once, then use this shortcut."),
            _ => Loc.T("Could not access the clipboard. Try copying the last dictation again.")
        };
        ShowActivationNotice(message);
    }
}
