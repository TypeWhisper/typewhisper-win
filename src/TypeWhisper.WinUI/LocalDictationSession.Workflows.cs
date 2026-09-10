using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private string _languageAtStart = "auto";
    private AutomaticWorkflowSnapshot? _workflowAtStart;
    private string? _targetHostAtStart;
    internal WorkflowLlmDefaults WorkflowDefaults { get; } = new(WinUIProfile.DataPath("workflow-llm-default.json"));

    // Called once on the UI thread after the original target process has been captured.
    private async Task CaptureWorkflowAtStartAsync()
    {
        _targetHostAtStart = null;
        try
        {
            var workflows = new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read();
            if (workflows.Any(workflow => workflow.IsEnabled && workflow.Trigger.HasWebsiteBindings &&
                workflow.Trigger.Kind is TypeWhisper.Core.Models.WorkflowTriggerKind.App or TypeWhisper.Core.Models.WorkflowTriggerKind.Website))
                _targetHostAtStart = await WindowsBrowserTargetReader.CaptureAsync(_target, (int)_targetProcessId,
                    _targetApp, _operationCancellation.Token);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            _workflowAtStart = AutomaticWorkflowSnapshot.Select(workflows, _targetApp, _targetHostAtStart, WorkflowDefaults.Resolve);
        }
        catch (OperationCanceledException) when (_operationCancellation.Token.IsCancellationRequested) { throw; }
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
