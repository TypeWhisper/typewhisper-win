using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

/// <summary>A local text model load that runs or last ran. Automatic loads follow a finished download.</summary>
internal sealed record LocalLlmLoadState(string PluginId, string ModelId, string ModelName, bool Automatic, bool Loading, string? Message);

internal sealed partial class LocalDictationSession
{
    internal ModelDownloadController LocalLlmDownload { get; } = new(ModelStorageSpace.DescribeFailure);
    internal string? LocalLlmDownloadPluginId { get; private set; }
    internal string? LocalLlmDownloadModelName { get; private set; }

    private readonly object _localLlmLoadSync = new();
    private CancellationTokenSource? _localLlmLoad;
    private Task _localLlmLoadDrain = Task.CompletedTask;
    private volatile LocalLlmLoadState? _localLlmLoadState;
    /// <summary>The running or last finished local text model load, for the plugin page and the LLM pickers.</summary>
    internal LocalLlmLoadState? LocalLlmLoad => _localLlmLoadState;
    /// <summary>Raised on any thread when a local text model load starts or ends.</summary>
    internal event Action? LocalLlmLoadChanged;

    internal async Task DownloadLocalLlmModelAsync(string pluginId, string modelId, string name)
    {
        if (!CanStartPluginSettingsAction || LocalLlmDownload.State.IsBusy || LocalLlmDownload.State.IsClosing)
            throw new InvalidOperationException(Loc.T("Finish the current model operation first."));
        if (ModelDownloadRefusal() is { } refused) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("model-download", refused);
        LocalLlmDownloadPluginId = pluginId;
        LocalLlmDownloadModelName = name;
        // A new download replaces the result of an earlier automatic load on the plugin page.
        if (_localLlmLoadState is { Loading: false }) PublishLocalLlmLoad(null);
        await LocalLlmDownload.RunAsync((progress, ct) => PluginRuntime.UseConfigurationAsync(pluginId, async (plugin, token) =>
        {
            if (plugin is not ILocalLlmModelManagement local || !local.LocalModels.Any(model => model.Model.Id == modelId))
                throw new InvalidOperationException(Loc.T("The local model provider changed."));
            // Rechecked under the lease: a policy that changed during the wait wins before any byte is downloaded.
            if (ModelDownloadRefusal() is { } refusedNow) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("model-download", refusedNow);
            await local.DownloadModelAsync(modelId, progress, token);
            return true;
        }, ct, preserveCompletedResult: true));
        if (!LocalLlmDownload.State.Succeeded) return;
        // As with Download & Load on the Mac, a downloaded model loads right away, so it can be chosen in a workflow at once.
        try { await LoadLocalLlmModelAsync(pluginId, modelId, name, CancellationToken.None, automatic: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* LocalLlmLoad carries the result for the plugin page. */ }
    }

    /// <summary>
    /// Loads a downloaded local text model; the runtime remembers it, so it loads again after an idle release or a
    /// restart. One load runs at a time, and a recording that starts cancels it. The provider snapshot is current on return.
    /// </summary>
    internal async Task LoadLocalLlmModelAsync(string pluginId, string modelId, string name, CancellationToken cancellationToken, bool automatic = false)
    {
        using var load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? refusal;
        lock (_localLlmLoadSync)
        {
            refusal = _disposed || _localLlmLoad is not null || LocalLlmDownload.State.IsBusy ? Loc.T("Finish the current model operation first.")
                : !CanStartSessionOperation ? Loc.T("Finish the current recording, then load the model.") : null;
            if (refusal is null) { _localLlmLoad = load; _localLlmLoadDrain = finished.Task; }
        }
        if (refusal is not null)
        {
            if (automatic) PublishLocalLlmLoad(new(pluginId, modelId, name, true, false, Loc.T("{0} is downloaded. Load it to use it in a workflow.", name)));
            throw new InvalidOperationException(refusal);
        }
        void CancelForRecording() => load.Cancel();
        RecordingStarting += CancelForRecording;
        PublishLocalLlmLoad(new(pluginId, modelId, name, automatic, true, null));
        var message = Loc.T("{0} could not be loaded.", name);
        try
        {
            await PluginRuntime.LoadLocalLlmModelAsync(pluginId, modelId, load.Token);
            // The pickers read the provider snapshot right after the load.
            await PluginRuntime.RefreshCapabilitiesAsync();
            message = Loc.T("{0} is loaded. Choose it in a workflow or as the default LLM.", name);
        }
        catch (OperationCanceledException) { message = Loc.T("Loading {0} was cancelled.", name); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { message = Loc.T("{0} could not be loaded: {1}", name, ex.Message); throw; }
        finally
        {
            RecordingStarting -= CancelForRecording;
            lock (_localLlmLoadSync) _localLlmLoad = null;
            PublishLocalLlmLoad(new(pluginId, modelId, name, automatic, false, message));
            finished.TrySetResult();
        }
    }

    /// <summary>Cancels the running local text model load, if any.</summary>
    internal void CancelLocalLlmLoad()
    {
        lock (_localLlmLoadSync)
        {
            try { _localLlmLoad?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    // Shutdown waits for a cancelled load before the runtime is released.
    private Task CancelAndDrainLocalLlmLoadAsync()
    {
        CancelLocalLlmLoad();
        lock (_localLlmLoadSync) return _localLlmLoadDrain;
    }

    private void PublishLocalLlmLoad(LocalLlmLoadState? state)
    {
        _localLlmLoadState = state;
        foreach (Action listener in LocalLlmLoadChanged?.GetInvocationList() ?? [])
            try { listener(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("local-llm.load.listener-failed", ex); }
    }
}
