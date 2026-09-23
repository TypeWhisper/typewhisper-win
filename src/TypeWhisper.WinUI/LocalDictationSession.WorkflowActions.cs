using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private PortablePluginAction? _workflowActionAtStart;
    private PortableMemoryProvider? _workflowMemoryAtStart;
    private PortableMemoryProvider? FindWorkflowMemory(string? id) => PluginRuntime.MemoryProviders.SingleOrDefault(p => p.PluginId == id);
    private async Task<IReadOnlyList<string>> RecallMemoryAsync(PortableMemoryProvider? source, string id, string query, CancellationToken ct)
    {
        if (source is null || source.PluginId != id)
            throw new InvalidOperationException("The selected memory source is unavailable. Enable its plugin or turn memory context off.");
        ct.ThrowIfCancellationRequested();
        await LocalLlmDownload.CancelAndDrainAsync();
        return await PluginRuntime.SearchMemoryAsync(source, query, ct);
    }

    private PortablePluginAction? FindWorkflowAction(string? pluginId) => string.IsNullOrWhiteSpace(pluginId)
        ? null : PluginRuntime.Actions.SingleOrDefault(action => action.PluginId == pluginId);

    private async Task<WorkflowActionResult> ExecuteWorkflowActionAsync(PortablePluginAction? action, string text,
        ActionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (action is null) return new(false, "The workflow action is unavailable. Enable its plugin and check its settings.");
        try
        {
            var result = await PluginRuntime.ExecuteActionAsync(action, text, context, ct);
            return new(result.Status == PortableActionStatus.Succeeded, result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(false, "The workflow action could not run. Check its destination before trying again.");
        }
    }

    internal async Task<(string Text, string? Message, bool? ActionSucceeded)> RunWorkflowWithActionAsync(Workflow workflow, string input, CancellationToken ct)
    {
        // Capture the exact enabled action before LLM processing; never substitute a reloaded plugin.
        var action = FindWorkflowAction(workflow.Output.TargetActionPluginId);
        var memory = FindWorkflowMemory(workflow.Behavior.MemoryPluginId);
        if (!string.IsNullOrWhiteSpace(workflow.Output.TargetActionPluginId) && action is null)
            throw new InvalidOperationException("The workflow action is unavailable. Enable its plugin first.");
        var text = await ManualWorkflowRunner.RunAsync(workflow, input,
            (provider, model) => LlmProviders.Any(p => p.SelectionId == provider && p.Ready && p.Models.Any(m => m.Id == model)),
            ProcessLlmAsync, ct, (id, query, token) => RecallMemoryAsync(memory, id, query, token));
        if (action is null) return (text, null, null);
        var result = await ExecuteWorkflowActionAsync(action, text, new ActionContext(null, null, null, null, input), ct);
        // Preserve known completion even if cancellation arrives after an external write.
        return (text, (result.Success ? "Completed: " : "Action not confirmed: ") + result.Message, result.Success);
    }
}
