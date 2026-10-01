using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private void InitializeRecentTranscriptionsShortcut() => _recentTranscriptionsShortcut = InitializeActionShortcut(GlobalShortcuts.RecentTranscriptions,
        OpenRecentTranscriptionsFromShortcut, 0x7A00, "recent-transcriptions-hotkeys.txt", Loc.T("Recent transcription shortcuts"), Loc.T("recent transcriptions"));

    private string? ChangeRecentTranscriptionsShortcut(string value) => ChangeActionShortcut(_recentTranscriptionsShortcut, Loc.T("Recent transcriptions"), value);

    private void OpenRecentTranscriptionsFromShortcut()
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        // The shortcut toggles the panel, as on macOS.
        if (_workflowPalette is { } open) { open.Dismiss(); return; }
        if (ShortcutAdmission.Rejection(Loc.T("recent transcriptions"), ShortcutActionBusy || _dictation.Models.Busy) is { } refusal)
        { ShowActivationNotice(refusal); return; }
        // As on macOS, this opens the recent-transcriptions palette for the app in front; History has its own window.
        OpenRecentTranscriptions(ForegroundWindowHistory.CurrentTarget);
    }
}
