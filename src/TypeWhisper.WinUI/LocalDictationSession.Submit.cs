using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Whether a physical Enter during this recording stops it and submits the text. The keyboard hook reads it
    // on the UI thread, so it must stay a plain field check.
    private readonly DictationSubmitIntent _submit = new();

    // A recording the user already stopped with the shortcut only inserts.
    internal bool CanRequestSubmit => !_disposed && _audio.IsRecording && !_stopPending && _earlyStopSamples is null && _submit.CanPress;

    // Why an Enter during recording reaches the app, as a diagnostic token.
    internal string SubmitUnavailableReason => !_audio.IsRecording ? "not-recording" : _stopPending ? "stopping"
        : _earlyStopSamples is not null ? "early-stop" : _submit.CanPress ? "available" : _submit.Requested ? "requested" : _submit.Arming.ToString();

    /// <summary>Records a physical Enter; the caller stops the recording when this returns true.</summary>
    internal bool RequestSubmit()
    {
        if (!CanRequestSubmit || !_submit.Press()) return false;
        AppDiagnostics.Write("dictation.submit-requested");
        return true;
    }

    // Shortcut and API workflows are known when capture begins. An automatic rule is usually matched just after,
    // so Enter is already taken while a matching rule could submit; the matched rule then decides.
    private DictationSubmitArming SubmitArmingAtCapture(AutomaticWorkflowSnapshot? workflow, bool ruleMatched) =>
        _setupOutputAtStart is not null ? DictationSubmitArming.Off : SubmitArmingFor(ruleMatched ? _workflowAtStart : workflow, ruleMatched);

    private DictationSubmitArming SubmitArmingFor(AutomaticWorkflowSnapshot? workflow, bool known = false)
    {
        if (workflow is not null || known)
            return workflow?.AutoEnterMode == WorkflowAutoEnterMode.DuringDictation ? DictationSubmitArming.Armed : DictationSubmitArming.Off;
        try
        {
            return AutomaticWorkflowSnapshot.MaySubmitDuringDictation(new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read())
                ? DictationSubmitArming.Pending : DictationSubmitArming.Off;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return DictationSubmitArming.Off; }
    }

    // A transcript kept for review after the window changed is never pasted, so it never submits either.
    private WorkflowAutoEnterMode AutoEnterModeAtStart => _reviewAfterTargetChange ? WorkflowAutoEnterMode.Never
        : _workflowAtStart?.AutoEnterMode ?? WorkflowAutoEnterMode.Never;
}
