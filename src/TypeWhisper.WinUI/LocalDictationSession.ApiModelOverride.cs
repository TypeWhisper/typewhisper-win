using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // The file pipeline owns _gate and _fileBusy until this scope is disposed.
    internal async Task<IAsyncDisposable?> BeginApiModelOverrideAsync(ParsedApiTranscription request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var modelId = request.Model;
        DictationProviderOption? provider = null;
        if (request.Engine is { Length: > 0 } engine)
            provider = ApiModelProvider(engine) ?? throw new LocalApiRequestException(404, "Transcription engine not found.");
        if (modelId is { Length: > 0 })
        {
            var qualified = DictationProviders.FirstOrDefault(item => modelId.StartsWith(item.Id + ":", StringComparison.Ordinal));
            if (qualified is not null)
            {
                if (provider is not null && provider.Id != qualified.Id)
                    throw new LocalApiRequestException(400, "The requested engine and model belong to different providers.");
                provider = qualified;
                modelId = modelId[(qualified.Id.Length + 1)..];
            }
            if (provider is null)
            {
                var matches = DictationProviders.Where(item => item.Models.Any(model => model.Id == modelId)).ToArray();
                provider = matches.FirstOrDefault(item => item.Id == _providerId)
                    ?? (matches.Length == 1 ? matches[0] : null);
                if (provider is null) throw new LocalApiRequestException(matches.Length == 0 ? 404 : 400,
                    matches.Length == 0 ? "Transcription model not found." : "Specify an engine for this ambiguous model ID.");
            }
        }
        provider ??= ApiModelProvider(_providerId)
            ?? throw new LocalApiRequestException(409, "Choose a transcription provider first.");
        if (!provider.Enabled) throw new LocalApiRequestException(409, "Enable this transcription provider first.");
        if (provider.Cloud && !provider.Configured) throw new LocalApiRequestException(409, "Configure this transcription provider first.");
        modelId ??= provider.SelectedModelId ?? provider.PreferredModelId
            ?? (request.AwaitDownload ? provider.Models.FirstOrDefault()?.Id : null);
        if (modelId is null) throw new LocalApiRequestException(409, "Choose a downloaded model, or set await_download to true.");
        var model = provider.Models.FirstOrDefault(item => item.Id == modelId)
            ?? throw new LocalApiRequestException(404, "Transcription model not found in this engine.");
        if (provider.Id == _providerId && modelId == ActiveModelId && IsReady) return null;
        if (!model.Ready && !request.AwaitDownload)
            throw new LocalApiRequestException(409, "The requested model is not downloaded. Set await_download to true to download it.");

        var previousProvider = _providerId;
        var previousModel = provider.Id == "local" ? Models.ActiveModelId : provider.SelectedModelId;
        var previousLocalSelection = Models.SelectedModelId;
        // The portable SDK cannot restore a null selection. Do not make a
        // persistent first-time selection as a side effect of one request.
        if (provider.Id != "local" && previousModel is null)
            throw new LocalApiRequestException(409, "Select an initial model for this provider before using a request-specific override.");
        var changedModel = false;
        var scope = new ApiModelOverrideScope(async () =>
        {
            try
            {
                if (!changedModel) return;
                if (provider.Id == "local")
                {
                    await Models.RestoreRequestModelAsync(previousModel, previousLocalSelection);
                }
                else
                {
                    var states = await PluginRuntime.GetModelStatesAsync(RegistrySelectionId(provider.Id), CancellationToken.None);
                    var previous = states.FirstOrDefault(item => item.ModelId == previousModel)
                        ?? throw new InvalidOperationException("The previous transcription model is no longer available for restoration.");
                    await PluginRuntime.SelectModelAsync(previous, CancellationToken.None);
                    await PluginRuntime.RefreshCapabilitiesAsync();
                }
            }
            finally { _providerId = previousProvider; }
        });
        try
        {
            if (provider.Id == "local")
            {
                if (!model.Ready) await Models.DownloadAsync(modelId, ct, propagateErrors: true);
                changedModel = true;
                await Models.ActivateAsync(modelId, ct, persistSelection: false);
            }
            else
            {
                var states = await PluginRuntime.GetModelStatesAsync(RegistrySelectionId(provider.Id), ct);
                var target = states.FirstOrDefault(item => item.ModelId == modelId)
                    ?? throw new LocalApiRequestException(404, "The requested model is no longer available.");
                if (target.SupportsDownload && !target.Downloaded)
                {
                    if (!request.AwaitDownload) throw new LocalApiRequestException(409, "Set await_download to true to download this model.");
                    await PluginRuntime.DownloadModelAsync(target, null, ct);
                }
                changedModel = true;
                await PluginRuntime.SelectModelAsync(target, ct);
                await PluginRuntime.RefreshCapabilitiesAsync();
            }
            ct.ThrowIfCancellationRequested();
            _providerId = provider.Id;
            if (!IsReady) throw new LocalApiRequestException(409, "The requested transcription model is not ready.");
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class ApiModelOverrideScope(Func<Task> restore) : IAsyncDisposable
    {
        private Func<Task>? _restore = restore;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _restore, null) is { } action) await action();
        }
    }
}
