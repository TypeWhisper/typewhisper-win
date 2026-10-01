using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// Local text models the user loaded stay available across idle releases and restarts: the host remembers
// the model and loads it again on the next request. An explicit unload or removal forgets it.
public sealed partial class PortablePluginRuntimeRegistry
{
    // Host-owned key in the package's settings, next to its Enabled preference.
    internal const string RestorableLocalLlmModelSetting = "RestorableLocalLlmModel";

    /// <summary>Releases idle local text models after this policy's delay; null keeps them loaded.</summary>
    public ModelIdleUnloadPolicy? IdleUnloadPolicy { get; init; }

    /// <summary>The local text model that loads automatically on the next request, or null.</summary>
    public string? RestorableLocalLlmModel(string pluginId)
    {
        Slot? slot;
        lock (_sync) _slots.TryGetValue(pluginId, out slot);
        return slot is null ? null : ReadRestorableModel(slot);
    }

    /// <summary>Loads a downloaded local text model and remembers it for automatic reloading.</summary>
    public Task LoadLocalLlmModelAsync(string pluginId, string modelId, CancellationToken cancellationToken = default) =>
        UseLocalLlmModelsAsync(pluginId, modelId, async (slot, local, token) =>
        {
            await local.LoadModelAsync(modelId, token).ConfigureAwait(false);
            slot.Services.SetSetting<string?>(RestorableLocalLlmModelSetting, modelId);
            TouchLocalLlm(slot, local);
        }, cancellationToken);

    /// <summary>Releases a loaded local text model; it does not load again until it is loaded explicitly.</summary>
    public Task UnloadLocalLlmModelAsync(string pluginId, string modelId, CancellationToken cancellationToken = default) =>
        UseLocalLlmModelsAsync(pluginId, modelId, async (slot, local, token) =>
        {
            slot.Services.SetSetting<string?>(RestorableLocalLlmModelSetting, null);
            if (local.LocalModels.Any(model => model.Model.Id == modelId && model.Loaded))
                await local.UnloadModelAsync(token).ConfigureAwait(false);
            if (!local.LocalModels.Any(model => model.Loaded)) slot.LocalLlmIdle?.Cancel();
        }, cancellationToken);

    /// <summary>Removes a local text model's files, releasing its memory first when necessary.</summary>
    public Task RemoveLocalLlmModelAsync(string pluginId, string modelId, CancellationToken cancellationToken = default) =>
        UseLocalLlmModelsAsync(pluginId, modelId, async (slot, local, token) =>
        {
            if (ReadRestorableModel(slot) == modelId) slot.Services.SetSetting<string?>(RestorableLocalLlmModelSetting, null);
            await local.RemoveModelAsync(modelId, token).ConfigureAwait(false);
            if (!local.LocalModels.Any(model => model.Loaded)) slot.LocalLlmIdle?.Cancel();
        }, cancellationToken);

    /// <summary>
    /// Starts the selected engine's stopped worker so its model loads while audio is still being recorded.
    /// In-process engines and running workers are unchanged; failures surface on the actual request.
    /// </summary>
    public Task PrepareTranscriptionWorkerAsync(string selectionId, CancellationToken cancellationToken = default)
    {
        IsolatedTranscriptionEngine? isolated;
        lock (_sync) isolated = _index.Transcription.TryGetValue(selectionId, out var role) && role.Owner.Accepting
            ? role.Engine as IsolatedTranscriptionEngine : null;
        return isolated?.PrepareAsync(cancellationToken) ?? Task.CompletedTask;
    }

    private Task UseLocalLlmModelsAsync(string pluginId, string modelId,
        Func<Slot, ILocalLlmModelManagement, CancellationToken, Task> use, CancellationToken cancellationToken)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_slots.TryGetValue(pluginId, out var slot) || slot.Package is null) throw new InvalidOperationException("Enable this plugin first.");
            owner = slot;
        }
        return UseAsync(owner, async token =>
        {
            if (owner.Package?.Plugin is not ILocalLlmModelManagement local || !local.LocalModels.Any(model => model.Model.Id == modelId))
                throw new InvalidOperationException("The local model provider changed.");
            await use(owner, local, token).ConfigureAwait(false);
            return true;
        }, cancellationToken, preserveCompletedResult: true);
    }

    // Callers hold _gate. Loads the remembered model before a request reaches an unloaded local provider.
    private static async Task RestoreLocalLlmAsync(Slot slot, ILlmProviderPlugin provider, CancellationToken token)
    {
        if (RestorableModel(slot, provider) is not { } model || slot.Package?.Plugin is not ILocalLlmModelManagement local) return;
        try { await local.LoadModelAsync(model.Id, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // A model that cannot load must not keep advertising itself as ready.
            slot.Services.SetSetting<string?>(RestorableLocalLlmModelSetting, null);
            throw;
        }
    }

    // Callers hold _gate. The remembered model advertises an unloaded local provider as ready.
    private static PluginModelInfo? RestorableModel(Slot slot, ILlmProviderPlugin provider)
    {
        if (!slot.IsLocal || provider.IsAvailable || slot.Package?.Plugin is not ILocalLlmModelManagement local
            || !ReferenceEquals(local, provider) || ReadRestorableModel(slot) is not { } id) return null;
        return local.LocalModels.FirstOrDefault(model => model.Model.Id == id && model.Downloaded && !model.Loaded)?.Model;
    }

    private static string? ReadRestorableModel(Slot slot)
    {
        try { return slot.Services.GetSetting<string>(RestorableLocalLlmModelSetting); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // Callers hold _gate. Restarts the idle wait while a local text model is loaded.
    private void TouchLocalLlm(Slot slot, ILocalLlmModelManagement local)
    {
        if (IdleUnloadPolicy is not { } policy || !slot.IsLocal) return;
        if (!local.LocalModels.Any(model => model.Loaded)) { slot.LocalLlmIdle?.Cancel(); return; }
        (slot.LocalLlmIdle ??= new(policy, () => ReleaseIdleLocalLlmAsync(slot))).Touch();
    }

    // Skips a release while any plugin operation runs; the timer retries it shortly afterwards.
    private async Task<bool> ReleaseIdleLocalLlmAsync(Slot slot)
    {
        if (!_gate.Wait(0)) return false;
        var released = false;
        try
        {
            lock (_sync) if (_disposed || !slot.Accepting) return true;
            if (slot.Package?.Plugin is not ILocalLlmModelManagement local || !local.LocalModels.Any(model => model.Loaded)) return true;
            slot.Services.Log(PluginLogLevel.Info, "Released the local text model after inactivity. The next request loads it again.");
            await local.UnloadModelAsync(CancellationToken.None).ConfigureAwait(false);
            released = true;
            return true;
        }
        finally
        {
            _gate.Release();
            if (released) QueueRefresh();
        }
    }
}
