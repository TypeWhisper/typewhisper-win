using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private Func<string, CancellationToken, Task<string>> ApiTranslationProcessor(string target)
    {
        var plan = LocalApiTranslation.Prepare(target, WorkflowDefaults.Read(), (providerId, modelId) =>
            LlmProviders.Any(provider => provider.SelectionId == providerId && provider.Ready
                && provider.Models.Any(model => model.Id == modelId)));
        return async (text, ct) =>
        {
            try { return await ProcessLlmAsync(plan.Provider, plan.Prompt, text, plan.Model, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { throw new LocalApiRequestException(502, "Translation failed. Check the default LLM provider and retry."); }
        };
    }
}
