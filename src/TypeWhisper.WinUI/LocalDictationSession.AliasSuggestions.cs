using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal async Task<IReadOnlyList<string>> SuggestAliasesAsync(string word, string language,
        string providerId, string modelId, CancellationToken ct)
    {
        if (!CanStartPluginSettingsAction || !_gate.Wait(0))
            throw new InvalidOperationException("Finish the current dictation or model operation before generating aliases.");
        _workflowReserved = true;
        try
        {
            Changed?.Invoke();
            return await DictionaryAliasSuggestions.GenerateAsync(word, language,
                (prompt, input, token) => PluginRuntime.UseLocalLlmAsync(providerId, (provider, providerToken) =>
                {
                    if (!provider.IsAvailable || !provider.SupportedModels.Any(model => model.Id == modelId))
                        throw new InvalidOperationException("The local model is no longer available. Load it in Plugins and try again.");
                    return provider.ProcessAsync(prompt, input, modelId, providerToken);
                }, token), ct);
        }
        finally
        {
            _workflowReserved = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }
}
