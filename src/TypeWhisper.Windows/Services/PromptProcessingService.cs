using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

public sealed class PromptProcessingService
{
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;

    public PromptProcessingService(PluginManager pluginManager, ISettingsService settings)
    {
        _pluginManager = pluginManager;
        _settings = settings;
    }

    public bool IsAnyProviderAvailable =>
        _pluginManager.LlmProviders.Any(p => p.IsAvailable);

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string inputText,
        string? providerOverride,
        string? modelOverride,
        CancellationToken ct)
    {
        var (provider, modelId) = ResolveProvider(providerOverride, modelOverride);
        if (provider is null)
            throw new InvalidOperationException(Loc.Instance["Error.NoLlmProvider"]);

        Debug.WriteLine($"[PromptProcessing] Using provider '{provider.ProviderName}' model '{modelId}' for workflow prompt");

        return await provider.ProcessAsync(systemPrompt, inputText, modelId, ct);
    }

    private (ILlmProviderPlugin? Provider, string ModelId) ResolveProvider(string? providerOverride, string? modelOverride)
    {
        // 1. Per-workflow override.
        if (!string.IsNullOrEmpty(providerOverride))
        {
            var result = ResolvePluginModelId(providerOverride, modelOverride);
            if (result.Provider is not null) return result;
        }

        // 2. Default LLM provider from settings
        var defaultProvider = _settings.Current.DefaultLlmProvider;
        if (!string.IsNullOrEmpty(defaultProvider))
        {
            var result = ResolvePluginModelId(defaultProvider, null);
            if (result.Provider is not null) return result;
        }

        // 3. First available provider
        foreach (var provider in _pluginManager.LlmProviders)
        {
            if (!provider.IsAvailable) continue;
            var firstModel = provider.SupportedModels.FirstOrDefault();
            if (firstModel is not null)
                return (provider, firstModel.Id);
        }

        return (null, "");
    }

    private (ILlmProviderPlugin? Provider, string ModelId) ResolvePluginModelId(string pluginModelId, string? modelOverride)
    {
        // Preferred format: plugin:{pluginId}:{modelId}
        var parts = pluginModelId.Split(':', 3);
        if (parts.Length >= 2 && parts[0] == "plugin")
        {
            var pluginId = parts[1];
            var modelId = parts.Length == 3 ? parts[2] : modelOverride;

            var provider = _pluginManager.LlmProviders
                .FirstOrDefault(p => p is ITypeWhisperPlugin twp &&
                    _pluginManager.GetPlugin(pluginId)?.Instance == twp &&
                    p.IsAvailable);

            if (provider is null)
                return (null, "");

            var resolvedModel = !string.IsNullOrWhiteSpace(modelId)
                ? modelId
                : provider.SupportedModels.FirstOrDefault()?.Id;

            return !string.IsNullOrWhiteSpace(resolvedModel)
                ? (provider, resolvedModel)
                : (null, "");
        }

        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            foreach (var provider in _pluginManager.LlmProviders.Where(p => p.IsAvailable))
            {
                if (provider.SupportedModels.Any(model => model.Id == modelOverride))
                    return (provider, modelOverride);
            }
        }

        return (null, "");
    }
}
