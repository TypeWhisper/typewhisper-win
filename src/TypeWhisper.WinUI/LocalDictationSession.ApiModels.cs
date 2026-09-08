using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal DictationProviderOption? ApiModelProvider(string engine) => DictationProviders.FirstOrDefault(provider =>
        provider.Id == engine || provider.PluginId == engine || ApiEngineId(provider) == engine);

    private string ApiEngineId(DictationProviderOption provider) => provider.Id == "local" ? "sherpa-onnx"
        : PluginRuntime.TranscriptionProviders.FirstOrDefault(item => item.SelectionId == RegistrySelectionId(provider.Id))?.EngineId ?? provider.Id;

    internal object ApiModelSnapshot() => new
    {
        status = IsReady ? CanTranscribeFile ? "ready" : "busy" : "no_model",
        engine = ActiveEngineId, model = ActiveModelId,
        models = DictationProviders.SelectMany(provider => provider.Models.Select(model =>
        {
            var registry = PluginRuntime.TranscriptionProviders.FirstOrDefault(item => item.SelectionId == RegistrySelectionId(provider.Id));
            var catalog = provider.Id == "local" ? Models.Models.FirstOrDefault(item => item.Model.Id == model.Id)?.Model
                : registry?.Models.FirstOrDefault(item => item.Id == model.Id);
            var state = registry?.ModelStates.FirstOrDefault(item => item.ModelId == model.Id);
            var active = provider.Id == ActiveProviderId && model.Id == ActiveModelId;
            return new
            {
                id = model.Id, full_id = provider.Id + ":" + model.Id, engine = ApiEngineId(provider), provider_id = provider.Id,
                name = model.Name, size_description = catalog?.SizeDescription ?? "", language_count = catalog?.LanguageCount ?? 0,
                status = !provider.Enabled ? "disabled" : !provider.Configured ? "not_configured" : model.Ready ? "ready" : "not_downloaded",
                active, selected = active, cloud = provider.Cloud,
                downloaded = provider.Cloud ? (bool?)null : provider.Id == "local" ? model.Ready : state?.Downloaded,
                // The portable SDK has no loaded-model query; configuration and selection do not prove residency.
                loaded = provider.Id == "local" ? (bool?)(Models.ActiveModelId == model.Id) : null
            };
        })).ToArray()
    };

    internal async Task<string?> ApiChangeModelAsync(string operation, DictationProviderOption provider, string? modelId, CancellationToken ct)
    {
        if (!CanChangeProvider || Models.Busy || !await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Finish dictation and model operations first.");
        try
        {
            SetStatus("Updating transcription model…", DictationPhase.Configuring);
            await _livePreview.StopAsync();
            ct.ThrowIfCancellationRequested();
            if (operation == "unload")
            {
                if (provider.Cloud) throw new NotSupportedException("This provider does not support explicit model unloading.");
                return provider.Id == "local" ? await Models.UnloadAsync(ct)
                    : await PluginRuntime.UnloadModelAsync(RegistrySelectionId(provider.Id), ct);
            }
            if (provider.Id == "local")
            {
                if (operation == "delete")
                {
                    if (!Models.Models.Any(item => item.Model.Id == modelId && item.Downloaded))
                        throw new LocalApiRequestException(404, "Downloaded model not found.");
                    await Models.RemoveAsync(modelId!, Models.Generation, ct);
                }
                else await Models.ActivateAsync(modelId!, ct);
            }
            else
            {
                var models = await PluginRuntime.GetModelStatesAsync(RegistrySelectionId(provider.Id), ct);
                var model = models.FirstOrDefault(item => item.ModelId == modelId)
                    ?? throw new InvalidOperationException("The model is no longer available.");
                if (operation == "delete")
                {
                    if (!model.SupportsRemoval) throw new NotSupportedException("This provider does not support removing model files.");
                    if (!model.Downloaded) throw new LocalApiRequestException(404, "Downloaded model not found.");
                    await PluginRuntime.RemoveModelAsync(model, ct);
                }
                else await PluginRuntime.SelectModelAsync(model, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (operation == "load")
            {
                _selection.SetSetting("Provider", provider.Id);
                _providerId = provider.Id;
            }
            return modelId;
        }
        finally
        {
            try { await PluginRuntime.RefreshCapabilitiesAsync(); }
            finally
            {
                _gate.Release();
                if (!_disposed) SetStatus(IsReady ? ActiveModelName + " ready" : "Choose a downloaded transcription model.", DictationPhase.Idle);
            }
        }
    }
}
