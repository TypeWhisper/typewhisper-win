namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private bool _workflowReserved;
    internal bool CanStartWorkflowShortcut => CanStartSessionOperation
        && (!PluginRuntime.IsBusy || LocalLlmDownload.State.IsBusy) && !Models.Busy;
    internal IDisposable ReserveWorkflowShortcut()
    {
        if (!CanStartWorkflowShortcut || !_gate.Wait(0))
            throw new InvalidOperationException("Finish the current recording, transcription or model operation before running a workflow shortcut.");
        _workflowReserved = true;
        try
        {
            Changed?.Invoke();
            return new WorkflowReservation(this);
        }
        catch
        {
            _workflowReserved = false;
            _gate.Release();
            throw;
        }
    }
    private sealed class WorkflowReservation(LocalDictationSession session) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            if (_released) return;
            _released = true; session._workflowReserved = false; session._gate.Release(); session.Changed?.Invoke();
        }
    }
}
