using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private void InitializeRecorderShortcut() => _recorderShortcut = InitializeActionShortcut(GlobalShortcuts.Recorder,
        OpenRecorderFromShortcut, 0x8800, "recorder-hotkeys.txt", "Recorder shortcuts", "recorder");

    private string? ChangeRecorderShortcut(string value) => ChangeActionShortcut(_recorderShortcut, "Recorder", value);

    private void OpenRecorderFromShortcut()
    {
        if (_closing || _profileRestoreClosing || ShortcutRecorder.AnyEditing) return;
        if (ShortcutAdmission.Rejection("the recorder", ShortcutActionBusy || WorkflowsView.IsBusy || _dictation.Models.Busy) is { } refusal)
        { ShowActivationNotice(refusal); return; }
        OpenRecorder(() => RecorderView.FocusEntry());
    }
}
