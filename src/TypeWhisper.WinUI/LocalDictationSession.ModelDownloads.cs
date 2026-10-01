using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private static string SessionProviderId(string selectionId) =>
        selectionId == CloudTranscriptionPlugin.PluginId ? "groq" : selectionId;

    internal bool IsRegistryModelSelected(PortableDownloadableModel model) =>
        RegistrySelectionId(_providerId) == model.SelectionId
        && ActiveRegistryProvider?.SelectedModelId == model.ModelId;

    internal ModelDownloadController RegistryModelDownload { get; } = new(ModelStorageSpace.DescribeFailure);
    internal PortableDownloadableModel? ActiveRegistryModelDownload { get; private set; }

    internal async Task<string?> DownloadRegistryModelAsync(PortableDownloadableModel model)
    {
        if (!Packages.Store.IsInstalled(model.PluginId)) return Loc.T("Install this plugin in Integrations first.");
        if (!CanChangeProvider || Models.Busy || RegistryModelDownload.State.IsClosing || !_gate.Wait(0))
            return Loc.T("Finish dictation and model operations before downloading a model.");
        ActiveRegistryModelDownload = model;
        try
        {
            SetStatus(Loc.T("Downloading {0}…", model.DisplayName), DictationPhase.Configuring);
            await RegistryModelDownload.RunAsync(async (progress, ct) =>
            {
                await _livePreview.StopAsync();
                ct.ThrowIfCancellationRequested();
                await PluginRuntime.DownloadModelAsync(model, progress, ct);
            });
            if (_disposed) return Loc.T("The application is shutting down.");
            await PluginRuntime.RefreshCapabilitiesAsync();
            return RegistryModelDownload.State.Succeeded ? null : RegistryModelDownload.State.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Loc.T("The model operation could not finish. Refresh its status and check the plugin's configuration.");
        }
        finally
        {
            ActiveRegistryModelDownload = null;
            _gate.Release();
            if (!_disposed)
                SetStatus(IsReady ? ModelReadyStatus() : Loc.T("Choose and configure a transcription provider in Dictation."), DictationPhase.Idle);
        }
    }

    internal Task CancelRegistryModelDownloadAsync() => RegistryModelDownload.CancelAndDrainAsync();

    internal async Task<string?> RemoveLocalModelAsync(string modelId, long expectedGeneration)
    {
        if (!CanChangeProvider || Models.Busy || RegistryModelDownload.State.IsClosing || !_gate.Wait(0))
            return Loc.T("Finish dictation and model operations before removing a model.");
        try
        {
            SetStatus(Loc.T("Removing model…"), DictationPhase.Configuring);
            await RegistryModelDownload.RunRemovalAsync(async ct =>
            {
                await _livePreview.StopAsync();
                ct.ThrowIfCancellationRequested();
                await Models.RemoveAsync(modelId, expectedGeneration, ct);
            });
            return RegistryModelDownload.State.Succeeded ? null : Models.Error ?? RegistryModelDownload.State.Message;
        }
        finally
        {
            _gate.Release();
            if (!_disposed) SetStatus(IsReady ? ModelReadyStatus() : Loc.T("Choose a downloaded model in Dictation."), DictationPhase.Idle);
        }
    }

    internal async Task<string?> RemoveRegistryModelAsync(PortableDownloadableModel model)
    {
        if (!Packages.Store.IsInstalled(model.PluginId)) return Loc.T("This plugin is no longer installed.");
        if (!CanChangeProvider || Models.Busy || RegistryModelDownload.State.IsClosing || !_gate.Wait(0))
            return Loc.T("Finish dictation and model operations before removing a model.");
        ActiveRegistryModelDownload = model;
        try
        {
            SetStatus(Loc.T("Removing {0}…", model.DisplayName), DictationPhase.Configuring);
            await RegistryModelDownload.RunRemovalAsync(async ct =>
            {
                await _livePreview.StopAsync();
                ct.ThrowIfCancellationRequested();
                await PluginRuntime.RemoveModelAsync(model, ct);
            });
            if (_disposed) return Loc.T("The application is shutting down.");
            await PluginRuntime.RefreshCapabilitiesAsync();
            return RegistryModelDownload.State.Succeeded ? null : RegistryModelDownload.State.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Loc.T("The model operation could not finish. Refresh its status before trying again.");
        }
        finally
        {
            ActiveRegistryModelDownload = null;
            _gate.Release();
            if (!_disposed)
                SetStatus(IsReady ? ModelReadyStatus() : Loc.T("Choose and configure a transcription provider in Dictation."), DictationPhase.Idle);
        }
    }

    internal async Task<string?> UseRegistryModelAsync(PortableDownloadableModel model)
    {
        string? saveError = null;
        var error = await ChangeRegistryPluginAsync(model.PluginId, async () =>
        {
            var ct = _operationCancellation.Begin();
            await PluginRuntime.SelectModelAsync(model, ct);
            await PluginRuntime.RefreshCapabilitiesAsync();
            if (_disposed) return;
            try
            {
                _selection.SetSetting("Provider", SessionProviderId(model.SelectionId));
                _providerId = SessionProviderId(model.SelectionId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                saveError = Loc.T("The plugin selected the model, but saving the dictation provider failed. Check the active provider in Dictation before recording.");
            }
        }, loadingModel: true);
        return error ?? saveError;
    }
}
