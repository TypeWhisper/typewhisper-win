using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private string _languageAtStart = "auto";
    private AutomaticWorkflowSnapshot? _workflowAtStart;

    // Called once on the UI thread after the original target process has been captured.
    private void CaptureWorkflowAtStart()
    {
        try
        {
            _workflowAtStart = AutomaticWorkflowSnapshot.Select(
                new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read(), _targetApp);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Automatic workflow catalog could not be read: " + ex.GetType().Name);
            _workflowAtStart = AutomaticWorkflowSnapshot.Unavailable();
        }
    }

    private Func<string, CancellationToken, Task<string>>? WorkflowProcessor(string? configuredLanguage, string? detectedLanguage)
    {
        var snapshot = _workflowAtStart;
        return snapshot is null ? null : (text, ct) => snapshot.ProcessAsync(text, configuredLanguage, detectedLanguage,
            (provider, model) => LlmProviders.Any(item => item.SelectionId == provider && item.Ready
                && item.Models.Any(candidate => candidate.Id == model)),
            ProcessLlmAsync, ct);
    }
}
