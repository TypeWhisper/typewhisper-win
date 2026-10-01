using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private void InitializeReadLastShortcut() => _readLastShortcut = InitializeActionShortcut(GlobalShortcuts.ReadLastTranscription,
        () => ReadLastTranscription(), 0x7E00, "read-last-transcription-hotkeys.txt", Loc.T("Read last transcription shortcuts"), Loc.T("read-last"));

    private string? ChangeReadLastShortcut(string value) => ChangeActionShortcut(_readLastShortcut, Loc.T("Read-last"), value);

    private long _readLastRevision;
    private async void ReadLastTranscription()
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        if (_dictationInitialization is not { IsCompleted: true } ||
            (!_dictation.SpokenFeedback.IsBusy && (!_dictation.CanChangeProvider || _dictation.Models.Busy)) ||
            _dictationInput?.IsRecordingOrStarting == true || _workflowTask is { IsCompleted: false })
        {
            var busy = Loc.T("Finish the current operation before reading the last dictation.");
            ShowActivationNotice(busy);
            return;
        }
        var revision = ++_readLastRevision;
        var result = await _dictation.ToggleReadLastDictationAsync();
        if (_closing || _profileRestoreClosing || revision != _readLastRevision) return;
        if (result.Status is SpokenFeedbackStatus.Failed or SpokenFeedbackStatus.Rejected)
        {
            ShowActivationNotice(result.Message ?? Loc.T("The last dictation could not be read aloud."));
        }
    }
}
