using TypeWhisper.Core.Interfaces;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private string _languageAtStart = "auto";
    private AutomaticWorkflowSnapshot? _workflowAtStart;
    private string? _targetHostAtStart;
    internal WorkflowLlmDefaults WorkflowDefaults { get; } = new(WinUIProfile.DataPath("workflow-llm-default.json"));

    // Called once on the UI thread after the original target process has been captured,
    // before microphone capture only when the matched rule decides whether recording can run.
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

    // An unreadable catalog also yields no rule after capture, so the global task applies.
    private bool AutomaticRuleDecidesTask(TranscriptionTask globalTask)
    {
        if (SupportsTranslation) return false;
        try { return WorkflowTranscriptionTask.AutomaticRuleDecidesTask(new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read(), globalTask, false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static string TargetProcessName(uint processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById((int)processId); return process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "Target app"; }
    }

    /// <summary>The task error that stopped the last start before microphone capture.</summary>
    internal string? TaskStartError { get; private set; }

    // Reports a configuration failure without treating it as cancellation:
    // a previously canceled recording may still own the operation token.
    private bool RejectTask(string? selectedTask, TranscriptionTask globalTask)
    {
        try
        {
            _taskAtStart = WorkflowTranscriptionTask.Resolve(selectedTask, globalTask, SupportsTranslation);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            TaskStartError = ex.Message;
            SetStatus(ex.Message, DictationPhase.Error);
            return true;
        }
    }

    private Func<string, CancellationToken, Task<string>>? WorkflowProcessor(string? configuredLanguage, string? detectedLanguage)
    {
        var snapshot = _workflowAtStart;
        var memory = _workflowMemoryAtStart;
        return snapshot is null ? null : (text, ct) => snapshot.ProcessAsync(text, configuredLanguage, detectedLanguage,
            (provider, model) => LlmProviders.Any(item => item.SelectionId == provider && item.Ready
                && item.Models.Any(candidate => candidate.Id == model)),
            ProcessLlmAsync, ct, (id, query, token) => RecallMemoryAsync(memory, id, query, token));
    }
}
