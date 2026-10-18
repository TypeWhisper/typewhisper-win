using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private Func<string, CancellationToken, Task<string>> ApiTranslationProcessor(string target, bool segmented = false)
    {
        var defaults = WorkflowDefaults.Read();
        // A default LLM the policy refuses answers 403 before availability is judged, since a refused package has
        // been unloaded and would otherwise read as merely unavailable.
        if (defaults is { } saved && (PluginRuntime.LlmRefusal(saved.Provider) ?? PluginRuntime.SelectionPackageRefusal(saved.Provider)) is { } refusedLlm)
            throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("llm", refusedLlm);
        var plan = LocalApiTranslation.Prepare(target, defaults, (providerId, modelId) =>
            LlmProviders.Any(provider => provider.SelectionId == providerId && provider.Ready
                && provider.Models.Any(model => model.Id == modelId)), segmented);
        return async (text, ct) =>
        {
            try { return await ProcessLlmAsync(plan.Provider, plan.Prompt, text, plan.Model, ct); }
            catch (OperationCanceledException) { throw; }
            // A refusal by machine policy is the documented 403, not an upstream failure.
            catch (TypeWhisper.Core.Models.ManagedPolicyDeniedException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { throw new LocalApiRequestException(502, "Translation failed. Check the default LLM provider and retry."); }
        };
    }
}
