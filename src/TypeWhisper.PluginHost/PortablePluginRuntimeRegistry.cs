using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>One installed package's runtime state. Enablement is separate from provider readiness.</summary>
public sealed record PortablePluginRuntimeState(string PluginId, bool Enabled, string? Error, bool HasApiKeySettings = false, bool HasTextSettings = false, bool ApiKeyConfigured = false);
/// <summary>A transcription role's UI snapshot, without exposing its package lifetime.</summary>
public sealed record PortableTranscriptionProvider(string PluginId, string SelectionId, string Name,
    bool Ready, string? SelectedModelId, IReadOnlyList<PluginModelInfo> Models, bool SupportsTranslation, bool SupportsPcm,
    IReadOnlyList<string>? SupportedLanguages = null, string? EngineId = null, bool SupportsLanguageHints = false)
{
    /// <summary>Actual per-model asset states captured under the package lease; independent of provider readiness.</summary>
    /// <summary>Local snapshot preview explicitly supported by the engine.</summary>
    public bool SupportsLocalLivePreview { get; init; }
    /// <summary>Live audio with an awaited, complete final transcript.</summary>
    public bool SupportsStreaming { get; init; }
    /// <summary>Downloaded asset snapshots.</summary>
    public IReadOnlyList<PortableDownloadableModel> ModelStates { get; init; } = [];
    /// <summary>Where the owning package processes audio, as declared by its manifest.</summary>
    public ContentLocality Locality { get; init; }
}
/// <summary>An LLM role's UI snapshot, without exposing its package lifetime.</summary>
public sealed record PortableLlmProvider(string PluginId, string SelectionId, string Name,
    bool Ready, IReadOnlyList<PluginModelInfo> Models)
{
    /// <summary>Whether the owning package declares local processing.</summary>
    public bool IsLocal { get; init; }
    /// <summary>Where the owning package processes text, as declared by its manifest.</summary>
    public ContentLocality Locality { get; init; }
}

/// <summary>An enabled text processor bound to one exact package activation.</summary>
public sealed record PortablePostProcessor(string PluginId, string Name, string Version, int Priority, long Generation);

/// <summary>An action identity bound to one exact package activation.</summary>
public sealed record PortablePluginAction(string PluginId, string ActionId, string Name, string Version, long Generation);

/// <summary>The known completion state of an explicitly invoked action.</summary>
public enum PortableActionStatus
{
    /// <summary>The plugin confirmed successful completion, even if cancellation arrived after its commit.</summary>
    Succeeded,
    /// <summary>The plugin explicitly reported failure.</summary>
    Failed,
    /// <summary>The invocation started but did not provide a reliable result; side effects may have occurred.</summary>
    CompletionUnknown
}

/// <summary>An action result without an automatically followed URL or retry instruction.</summary>
public sealed record PortableActionOutcome(PortableActionStatus Status, string Message);

/// <summary>
/// Owns each activated package once and serializes all capability/configuration calls.
/// The host initializes package storage first and must route runtime calls through this owner.
/// </summary>
public sealed partial class PortablePluginRuntimeRegistry(PortablePluginStore store, Version hostVersion,
    Func<string, IPluginHostServices> createServices, Func<string, bool>? ownsPlugin = null,
    Func<ManagedProviderPolicy>? policy = null) : IAsyncDisposable
{
    private sealed class Slot(string id, IPluginHostServices services)
    {
        internal readonly string Id = id;
        internal readonly IPluginHostServices Services = services;
        internal PortablePluginPackage? Package;
        internal bool Accepting;
        internal bool IsLocal;
        // Declared by the manifest; Unknown counts as not local under restrictive policy.
        internal ContentLocality Locality = ContentLocality.Unknown;
        internal IReadOnlyList<string>? Categories;
        internal long Generation;
        internal string? Error;
        // Whether Error names a policy refusal; such a package loads again once the policy allows it.
        internal bool PolicyRefused;
        // Marked for a policy unload that has not run yet: no capability rebuild touches the package meanwhile.
        internal bool Stopping;
        internal bool CapabilityError;
        internal bool ApiKeyConfigured;
        internal CancellationTokenSource? Request;
        internal Task CancellationCallbacks = Task.CompletedTask;
        internal string? Directory;
        // Releases this activation's idle local text model; created once a model is loaded.
        internal ModelIdleTimer? LocalLlmIdle;
        // Worker adapters for this activation, keyed by the in-process engine they present. Locked for readers
        // outside the gate; only gate holders change it.
        internal readonly Dictionary<ITranscriptionEnginePlugin, IsolatedTranscriptionEngine> Isolated = new(ReferenceEqualityComparer.Instance);
    }
    private sealed record TranscriptionRole(Slot Owner, ITranscriptionEnginePlugin Engine);
    private sealed record LlmRole(Slot Owner, ILlmProviderPlugin Provider);
    private sealed record ActionRole(Slot Owner, IActionPlugin Action, PortablePluginAction Snapshot);
    private sealed record PostProcessorRole(Slot Owner, IPostProcessorPlugin Processor, PortablePostProcessor Snapshot);
    private sealed record Index(Dictionary<string, TranscriptionRole> Transcription, Dictionary<string, LlmRole> Llm,
        PortableTranscriptionProvider[] TranscriptionSnapshots, PortableLlmProvider[] LlmSnapshots)
    {
        internal Dictionary<string, PostProcessorRole> PostProcessors { get; init; } = new(StringComparer.Ordinal);
        internal PortableTtsProvider[] TtsSnapshots { get; init; } = [];
        internal Dictionary<string, ActionRole> Actions { get; init; } = new(StringComparer.Ordinal);
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private Index _index = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], []);
    private bool _disposed;
    private int _refreshRequested;
    private int _refreshRunning;

    /// <summary>Raised after state changes; UI subscribers must dispatch to their UI thread.</summary>
    public event Action? Changed;
    /// <summary>The machine policy consulted before packages load and before content reaches a plugin.</summary>
    private ManagedProviderPolicy Policy => policy?.Invoke() ?? ManagedProviderPolicy.None;

    /// <summary>Why policy refuses a package as a whole, or null. A package the registry has not seen is judged by the allowlist only.</summary>
    public string? PackageRefusal(string pluginId)
    {
        lock (_sync) return _slots.TryGetValue(pluginId, out var slot) && slot.Categories is not null
            ? Policy.PackageRefusal(pluginId, slot.Locality, slot.Categories) : Policy.PluginRefusal(pluginId);
    }

    /// <summary>Why policy refuses audio for an enabled transcription role, or null. Unknown roles return null.</summary>
    public string? TranscriptionRefusal(string selectionId)
    {
        lock (_sync) return _index.Transcription.TryGetValue(selectionId, out var role)
            ? Policy.TranscriptionRefusal(selectionId, role.Owner.Id, role.Owner.Locality) : null;
    }

    /// <summary>
    /// Why policy refuses prompt text, such as dictionary terms, alongside audio for an enabled transcription role,
    /// or null. Audio itself follows <see cref="TranscriptionRefusal"/>. Unknown roles return null.
    /// </summary>
    public string? TranscriptionPromptRefusal(string selectionId)
    {
        lock (_sync) return _index.Transcription.TryGetValue(selectionId, out var role)
            ? Policy.TextTransferRefusal(role.Owner.Id, role.Owner.Locality) : null;
    }

    /// <summary>Why policy refuses text for an enabled LLM role, or null. Unknown roles return null.</summary>
    public string? LlmRefusal(string selectionId)
    {
        lock (_sync) return _index.Llm.TryGetValue(selectionId, out var role)
            ? Policy.LlmRefusal(selectionId, role.Owner.Id, role.Owner.Locality) : null;
    }

    /// <summary>Why policy refuses handing text to an enabled package, or null. Unknown packages return null.</summary>
    public string? TextTransferRefusal(string pluginId)
    {
        lock (_sync) return _slots.TryGetValue(pluginId, out var slot) ? Policy.TextTransferRefusal(pluginId, slot.Locality) : null;
    }
    /// <summary>Runs selected local engines in worker processes; null keeps every engine in process.</summary>
    public TranscriptionIsolation? TranscriptionIsolation { get; init; }
    /// <summary>Whether a serialized runtime operation is currently executing.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Returns the last published metadata snapshots of enabled transcription roles.</summary>
    public IReadOnlyList<PortableTranscriptionProvider> TranscriptionProviders
    {
        get { lock (_sync) return _index.TranscriptionSnapshots.Where(item => _slots[item.PluginId].Accepting)
            .Select(item => item with { ModelStates = Array.AsReadOnly(item.ModelStates.Select(model => model with
                { Generation = _slots[item.PluginId].Generation }).ToArray()) }).ToArray(); }
    }
    /// <summary>Returns the last published metadata snapshots of enabled LLM roles.</summary>
    public IReadOnlyList<PortableLlmProvider> LlmProviders
    {
        get { lock (_sync) return _index.LlmSnapshots.Where(item => _slots[item.PluginId].Accepting).ToArray(); }
    }
    /// <summary>Returns enabled processors in deterministic priority and identifier order.</summary>
    public IReadOnlyList<PortablePostProcessor> PostProcessors
    {
        get
        {
            lock (_sync) return _index.PostProcessors.Values.Where(role => role.Owner.Accepting)
                .Select(role => role.Snapshot with { Generation = role.Owner.Generation }).OrderBy(item => item.Priority)
                .ThenBy(item => item.PluginId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Returns explicit manual actions; listing never invokes an action.</summary>
    public IReadOnlyList<PortablePluginAction> Actions
    {
        get
        {
            lock (_sync) return _index.Actions.Values.Where(role => role.Owner.Accepting)
                .Select(role => role.Snapshot with { Generation = role.Owner.Generation })
                .OrderBy(item => item.PluginId, StringComparer.Ordinal).ThenBy(item => item.ActionId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Returns known installed packages and their visible activation/capability errors.</summary>
    public IReadOnlyList<PortablePluginRuntimeState> Snapshot()
    {
        lock (_sync) return _slots.Values.Select(slot => new PortablePluginRuntimeState(slot.Id, slot.Accepting, slot.Error, slot.Package?.Plugin is IApiKeyPlugin, slot.Package?.Plugin is IPluginTextSettings or IPluginSettingsActions, slot.Package is not null && slot.ApiKeyConfigured)).ToArray();
    }

    /// <summary>Restores only explicit saved enablement. Missing preferences never activate a package.</summary>
    public async Task InitializeAsync()
    {
        if (!store.Initialized) throw new InvalidOperationException("Initialize plugin storage before the runtime.");
        foreach (var package in store.Inventory())
        {
            if (package.Manifest is not { } manifest) continue;
            if (ownsPlugin?.Invoke(manifest.Id) == false) continue;
            // A package policy does not allow is never loaded; its slot only carries the reason.
            var refused = Policy.PackageRefusal(manifest.Id, PluginLocalityMap.ToContentLocality(manifest.ResolvedLocality), manifest.ResolvedCategories);
            Slot slot;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_slots.TryGetValue(manifest.Id, out slot!))
                    _slots.Add(manifest.Id, slot = new(manifest.Id, new RuntimePluginHostServices(createServices(manifest.Id), QueueRefresh)));
                slot.Locality = PluginLocalityMap.ToContentLocality(manifest.ResolvedLocality);
                slot.Categories = manifest.ResolvedCategories ?? [];
                slot.Error = package.Error ?? refused;
                slot.PolicyRefused = package.Error is null && refused is not null;
            }
            if (package.Error is not null || refused is not null) continue;
            try
            {
                if (slot.Services.GetSetting<bool?>("Enabled") == true) await SetEnabledAsync(slot.Id, true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { lock (_sync) slot.Error = "Plugin enablement could not be loaded: " + ex.GetType().Name; }
        }
        NotifyChanged();
    }

    /// <summary>
    /// Enables a verified installed package, or cancels and drains it before disabling.
    /// Returns a visible error; failed persistence does not report a changed preference.
    /// </summary>
    public async Task<string?> SetEnabledAsync(string pluginId, bool enabled)
    {
        if (ownsPlugin?.Invoke(pluginId) == false) return "This package is managed by its existing runtime owner.";
        Slot slot;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_slots.TryGetValue(pluginId, out slot!))
            {
                if (!store.IsInstalled(pluginId)) return "This plugin is not installed.";
                _slots.Add(pluginId, slot = new(pluginId, new RuntimePluginHostServices(createServices(pluginId), QueueRefresh)));
            }
            if (enabled && slot.Accepting) return null;
            // Policy is checked before any code of the package loads; disabling is always allowed.
            if (enabled && PackageRefusal(pluginId) is { } refused) { slot.Error = refused; slot.PolicyRefused = true; return refused; }
            generation = ++slot.Generation;
            slot.Accepting = false;
            CancelRequest(slot);
        }
        await _gate.WaitAsync();
        try
        {
            lock (_sync) if (_disposed || slot.Generation != generation) return "This plugin operation was superseded.";
            if (!enabled)
            {
                try { slot.Services.SetSetting("Enabled", false); }
                catch
                {
                    lock (_sync) slot.Accepting = slot.Package is not null && !_disposed && slot.Generation == generation;
                    throw;
                }
                var old = slot.Package; slot.Package = null;
                try { Publish(BuildIndex()); }
                catch { Publish(new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], [])); throw; }
                finally { if (old is not null) await ReleasePackageAsync(slot, old); }
                lock (_sync) { slot.Error = null; slot.PolicyRefused = false; }
                return null;
            }
            var directory = store.Resolve(pluginId); // Inventory paths are display identities, not loadable directories.
            var inspection = PortablePluginInventory.Inspect(directory, hostVersion);
            if (inspection.Error is { } invalid) throw new InvalidDataException(invalid);
            if (inspection.Manifest?.Id != pluginId) throw new InvalidDataException("Installed package identity does not match its registration.");
            // Judged again with the manifest on disk and the policy of this moment, before any package code loads:
            // the wait for the gate may have outlasted a policy change. A package still held from an earlier
            // activation is released rather than kept beside the refusal.
            var locality = PluginLocalityMap.ToContentLocality(inspection.Manifest.ResolvedLocality);
            var categories = inspection.Manifest.ResolvedCategories ?? [];
            lock (_sync) { slot.Locality = locality; slot.Categories = categories; }
            if (Policy.PackageRefusal(pluginId, locality, categories) is { } refusedPackage)
            {
                var stale = slot.Package; slot.Package = null;
                lock (_sync) { slot.Error = refusedPackage; slot.PolicyRefused = true; }
                if (stale is not null) { try { Publish(BuildIndex()); } finally { await ReleasePackageAsync(slot, stale); } }
                return refusedPackage;
            }
            var loadedHere = slot.Package is null;
            var package = slot.Package ?? await PortablePluginPackage.LoadAsync(directory, slot.Services, hostVersion);
            // Judged once more after activation: a policy that changed during the await releases the package
            // instead of publishing it.
            if (Policy.PackageRefusal(pluginId, locality, categories) is { } refusedAfterLoad)
            {
                slot.Package = null;
                lock (_sync) { slot.Error = refusedAfterLoad; slot.PolicyRefused = true; }
                await ReleasePackageAsync(slot, package);
                return refusedAfterLoad;
            }
            slot.Package = package;
            slot.Directory = directory;
            // Locality is authoritative; the legacy flag only feeds it for manifests that predate the field.
            slot.IsLocal = inspection.Manifest.ResolvedLocality == PluginLocality.Local;
            try
            {
                var next = BuildIndex();
                lock (_sync)
                {
                    if (_disposed || slot.Generation != generation) throw new OperationCanceledException("Plugin activation was superseded.");
                    slot.Services.SetSetting("Enabled", true);
                    slot.Accepting = true;
                    slot.Stopping = false;
                    slot.Error = null;
                    slot.PolicyRefused = false;
                    _index = next;
                }
            }
            catch
            {
                slot.Package = null;
                if (loadedHere) await ReleasePackageAsync(slot, package);
                else slot.Package = package;
                throw;
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = ex is CapabilityCollisionException ? ex.Message : "Plugin operation failed: " + ex.GetType().Name;
            lock (_sync) slot.Error = message;
            return message;
        }
        finally { _gate.Release(); NotifyChanged(); }
    }

    private string? SlotRefusal(Slot slot) => slot.Categories is null
        ? Policy.PluginRefusal(slot.Id) : Policy.PackageRefusal(slot.Id, slot.Locality, slot.Categories);

    /// <summary>
    /// Re-evaluates the policy after it changed. A loaded package the policy now refuses is unloaded while its saved
    /// preference stays as it is; a package whose refusal was lifted loads again when that preference enables it.
    /// A running operation of a role the policy now refuses is cancelled; new calls are judged at their lease. One
    /// package that fails to unload does not stop enforcement for the others. Returns the IDs whose state changed.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReapplyPolicyAsync()
    {
        List<(Slot Slot, string? Refused, long? Marked)> slots;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Every refused package stops accepting at once, before any unload waits for the gate: a queued call of
            // a later package then fails at its lease instead of running the package's code.
            slots = _slots.Values.Select(slot =>
            {
                var refused = SlotRefusal(slot);
                long? marked = null;
                if (refused is not null && slot.Package is not null)
                {
                    marked = ++slot.Generation;
                    slot.Accepting = false;
                    slot.Stopping = true;
                    CancelRequest(slot);
                }
                return (slot, refused, marked);
            }).ToList();
            CancelRefusedRoles();
        }
        var changed = new List<string>();
        foreach (var (slot, refused, marked) in slots)
        {
            if (refused is not null)
            {
                if (marked is { } generation)
                {
                    try { if (await UnloadMarkedAsync(slot, generation)) changed.Add(slot.Id); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // The package is detached before its release can fail: the refusal stands and the others follow.
                        changed.Add(slot.Id);
                    }
                }
                lock (_sync) if (slot.Package is null && (slot.Error is null || slot.PolicyRefused)) { slot.Error = refused; slot.PolicyRefused = true; }
                continue;
            }
            bool reload;
            lock (_sync)
            {
                reload = slot.PolicyRefused && slot.Package is null;
                if (reload) { slot.PolicyRefused = false; slot.Error = null; }
            }
            if (!reload) continue;
            try
            {
                if (slot.Services.GetSetting<bool?>("Enabled") == true && await SetEnabledAsync(slot.Id, true) is null) changed.Add(slot.Id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { lock (_sync) slot.Error = "Plugin enablement could not be loaded: " + ex.GetType().Name; }
        }
        NotifyChanged();
        return changed;
    }

    // Cancels the running request of a package whose active role the policy now refuses. A package that serves
    // several roles loses its current request even when the running role is still allowed; its next call is judged
    // again at the lease. Callers hold _sync.
    private void CancelRefusedRoles()
    {
        var policy = Policy;
        // A transcription may carry dictionary terms as a prompt, so the text rule cancels a running one as well.
        foreach (var (selectionId, role) in _index.Transcription)
            if (policy.TranscriptionRefusal(selectionId, role.Owner.Id, role.Owner.Locality) is not null
                || policy.TextTransferRefusal(role.Owner.Id, role.Owner.Locality) is not null)
                CancelRequest(role.Owner);
        foreach (var (selectionId, role) in _index.Llm)
            if (policy.LlmRefusal(selectionId, role.Owner.Id, role.Owner.Locality) is not null) CancelRequest(role.Owner);
        // Text reaches processors, actions, speech and memory sources: judged by the activated plugin, since a
        // manifest need not declare categories.
        foreach (var slot in _slots.Values)
            if (slot.Package?.Plugin is IPostProcessorPlugin or IActionPlugin or ITtsProviderPlugin or IMemoryStoragePlugin
                && policy.TextTransferRefusal(slot.Id, slot.Locality) is not null)
                CancelRequest(slot);
    }

    // The disable path without the preference write for a slot ReapplyPolicyAsync marked: the user's choice
    // survives the policy. False when a newer operation superseded the mark or the policy allowed the package again
    // meanwhile.
    private async Task<bool> UnloadMarkedAsync(Slot slot, long generation)
    {
        await _gate.WaitAsync();
        try
        {
            string? refused;
            lock (_sync)
            {
                if (_disposed || slot.Generation != generation) return false;
                // Judged again under the gate: a policy that relaxed during the wait keeps the package running.
                refused = SlotRefusal(slot);
                slot.Stopping = false;
                if (refused is null) { slot.Accepting = slot.Package is not null; return false; }
                slot.Error = refused; slot.PolicyRefused = true;
            }
            var old = slot.Package; slot.Package = null;
            try { Publish(BuildIndex()); }
            catch { Publish(new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], [])); throw; }
            finally { if (old is not null) await ReleasePackageAsync(slot, old); }
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Uses a transcription role while its package is retained. Do not retain SDK references
    /// beyond the callback; cancellation and superseded results are checked before publication.
    /// </summary>
    public Task<T> UseTranscriptionAsync<T>(string selectionId,
        Func<ITranscriptionEnginePlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_index.Transcription.TryGetValue(selectionId, out var role)) throw new InvalidOperationException("Transcription provider is unavailable.");
            owner = role.Owner;
        }
        // Checked before queuing and again once the lease is held, so a policy that arrived meanwhile still wins.
        DenyTranscriptionIfRefused(selectionId, owner);
        return UseAsync(owner, token =>
        {
            DenyTranscriptionIfRefused(selectionId, owner);
            return _index.Transcription.TryGetValue(selectionId, out var current) && current.Owner == owner
                ? use(current.Engine, token) : throw new InvalidOperationException("Transcription provider changed.");
        }, cancellationToken);
    }

    private void DenyTranscriptionIfRefused(string selectionId, Slot owner)
    {
        if (Policy.TranscriptionRefusal(selectionId, owner.Id, owner.Locality) is { } refused)
            throw new ManagedPolicyDeniedException("transcription", refused);
    }

    private void DenyLlmIfRefused(string selectionId, Slot owner)
    {
        if (Policy.LlmRefusal(selectionId, owner.Id, owner.Locality) is { } refused)
            throw new ManagedPolicyDeniedException("llm", refused);
    }

    private void DenyTextTransferIfRefused(Slot owner, string capability)
    {
        if (Policy.TextTransferRefusal(owner.Id, owner.Locality) is { } refused)
            throw new ManagedPolicyDeniedException(capability, refused);
    }

    /// <summary>Uses an LLM role within its owning package's serialized request lifetime.</summary>
    public Task<T> UseLlmAsync<T>(string selectionId,
        Func<ILlmProviderPlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
        => UseLlmCoreAsync(selectionId, use, false, cancellationToken);

    /// <summary>Uses an LLM only when its active package declares local processing; never falls back to cloud.</summary>
    public Task<T> UseLocalLlmAsync<T>(string selectionId,
        Func<ILlmProviderPlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
        => UseLlmCoreAsync(selectionId, use, true, cancellationToken);

    private Task<T> UseLlmCoreAsync<T>(string selectionId,
        Func<ILlmProviderPlugin, CancellationToken, Task<T>> use, bool requireLocal, CancellationToken cancellationToken)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_index.Llm.TryGetValue(selectionId, out var role)) throw new InvalidOperationException("LLM provider is unavailable.");
            owner = role.Owner;
        }
        DenyLlmIfRefused(selectionId, owner);
        return UseAsync(owner, async token =>
        {
            if (!_index.Llm.TryGetValue(selectionId, out var current) || current.Owner != owner)
                throw new InvalidOperationException("LLM provider changed.");
            DenyLlmIfRefused(selectionId, owner);
            if (requireLocal && !owner.IsLocal)
                throw new InvalidOperationException("Choose a local LLM for alias suggestions.");
            await RestoreLocalLlmAsync(owner, current.Provider, token).ConfigureAwait(false);
            try { return await use(current.Provider, token).ConfigureAwait(false); }
            finally { if (owner.Package?.Plugin is ILocalLlmModelManagement local) TouchLocalLlm(owner, local); }
        }, cancellationToken);
    }

    /// <summary>Processes text only with the exact activation captured at operation start.</summary>
    public Task<string> ProcessTextAsync(PortablePostProcessor expected, string text,
        PostProcessingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Slot owner;
        lock (_sync)
        {
            if (!_index.PostProcessors.TryGetValue(expected.PluginId, out var role)
                || (role.Snapshot with { Generation = role.Owner.Generation }) != expected || !role.Owner.Accepting || role.Owner.Generation != expected.Generation)
                throw new InvalidOperationException("The captured text processor is no longer available.");
            owner = role.Owner;
        }
        DenyTextTransferIfRefused(owner, "text-processor");
        return UseAsync(owner, token =>
        {
            if (!_index.PostProcessors.TryGetValue(expected.PluginId, out var current)
                || current.Owner != owner || (current.Snapshot with { Generation = owner.Generation }) != expected || owner.Generation != expected.Generation)
                throw new InvalidOperationException("The captured text processor changed.");
            DenyTextTransferIfRefused(owner, "text-processor");
            return current.Processor.ProcessAsync(text, context, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Invokes one manually selected action. Cancellation before invocation prevents it; after invocation,
    /// a confirmed result is preserved and an exception reports uncertain completion. No URL is opened or action retried.
    /// </summary>
    public Task<PortableActionOutcome> ExecuteActionAsync(PortablePluginAction expected, string text,
        ActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Slot owner;
        lock (_sync)
        {
            if (!_index.Actions.TryGetValue(expected.PluginId, out var role) || !role.Owner.Accepting
                || (role.Snapshot with { Generation = role.Owner.Generation }) != expected)
                throw new InvalidOperationException("The selected action is no longer available.");
            owner = role.Owner;
        }
        DenyTextTransferIfRefused(owner, "action");
        return UseAsync(owner, async token =>
        {
            if (!_index.Actions.TryGetValue(expected.PluginId, out var current) || current.Owner != owner
                || (current.Snapshot with { Generation = owner.Generation }) != expected)
                throw new InvalidOperationException("The selected action changed before execution.");
            DenyTextTransferIfRefused(owner, "action");
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await current.Action.ExecuteAsync(text, context, token).ConfigureAwait(false);
                return new PortableActionOutcome(result.Success ? PortableActionStatus.Succeeded : PortableActionStatus.Failed,
                    string.IsNullOrWhiteSpace(result.Message) ? result.Success ? "Action completed." : "The action reported a failure." : result.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new PortableActionOutcome(PortableActionStatus.CompletionUnknown,
                    "The action's completion could not be confirmed. Check its destination before running it again.");
            }
        }, cancellationToken, preserveCompletedResult: true);
    }

    /// <summary>
    /// Serializes host-rendered configuration with requests, including IApiKeyPlugin operations.
    /// The callback must not retain the plugin reference or activate/dispose the plugin itself.
    /// Set preserveCompletedResult for write operations whose completion metadata must survive late cancellation.
    /// Read-only snapshots may suppress the completion refresh to avoid notification feedback loops.
    /// </summary>
    public Task<T> UseConfigurationAsync<T>(string pluginId,
        Func<ITypeWhisperPlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default,
        bool preserveCompletedResult = false, bool refreshCapabilities = true)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_slots.TryGetValue(pluginId, out var slot) || slot.Package is null) throw new InvalidOperationException("Enable this plugin first.");
            owner = slot;
        }
        return UseAsync(owner, token =>
        {
            // Configuration runs package code: judged again under the lease, like content operations.
            if (SlotRefusal(owner) is { } refused) throw new ManagedPolicyDeniedException("plugin", refused);
            return use(owner.Package!.Plugin, token);
        }, cancellationToken, preserveCompletedResult, refreshCapabilities);
    }

    private async Task<T> UseAsync<T>(Slot slot, Func<CancellationToken, Task<T>> use, CancellationToken cancellationToken, bool preserveCompletedResult = false, bool refreshCapabilities = true)
    {
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!slot.Accepting) throw new InvalidOperationException("This plugin is disabled or stopping.");
            generation = slot.Generation;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? request = null;
        try
        {
            lock (_sync)
            {
                if (_disposed || !slot.Accepting || generation != slot.Generation) throw new OperationCanceledException("Plugin selection changed.");
                request = slot.Request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            var result = await use(request.Token).ConfigureAwait(false);
            if (!preserveCompletedResult)
            {
                request.Token.ThrowIfCancellationRequested();
                lock (_sync)
                    if (_disposed || !slot.Accepting || generation != slot.Generation) throw new OperationCanceledException("Plugin selection changed.");
            }
            return result;
        }
        finally
        {
            Task callbacks;
            lock (_sync) { slot.Request = null; callbacks = slot.CancellationCallbacks; slot.CancellationCallbacks = Task.CompletedTask; }
            await callbacks.ConfigureAwait(false);
            request?.Dispose();
            _gate.Release();
            if (refreshCapabilities) QueueRefresh();
        }
    }

    /// <summary>What a plugin's transcription worker last reported about how it runs, or null before a worker ran.</summary>
    public TranscriptionAccelerationStatus? IsolatedAccelerationStatus(string pluginId)
    {
        Slot? slot;
        lock (_sync) _slots.TryGetValue(pluginId, out slot);
        if (slot is null) return null;
        lock (slot.Isolated) return slot.Isolated.Values.Select(engine => engine.ReportedAccelerationStatus).FirstOrDefault(status => status is not null);
    }

    /// <summary>Ends the plugin's transcription workers, which release their models and model files.</summary>
    public async Task StopTranscriptionWorkersAsync(string pluginId)
    {
        Slot? slot;
        lock (_sync) _slots.TryGetValue(pluginId, out slot);
        if (slot is null) return;
        IsolatedTranscriptionEngine[] isolated;
        lock (slot.Isolated) isolated = slot.Isolated.Values.ToArray();
        foreach (var engine in isolated) await engine.UnloadModelAsync().ConfigureAwait(false);
    }

    /// <summary>Refreshes capability indices after plugin notifications without invoking plugins under the state lock.</summary>
    public async Task RefreshCapabilitiesAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) if (_disposed) return;
            Publish(BuildIndex());
            lock (_sync)
                foreach (var slot in _slots.Values.Where(slot => slot.CapabilityError)) { slot.Error = null; slot.CapabilityError = false; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_sync)
            {
                foreach (var slot in _slots.Values.Where(slot => slot.Package is not null))
                {
                    slot.Error = ex is CapabilityCollisionException ? ex.Message : "Plugin capabilities could not be refreshed: " + ex.GetType().Name;
                    slot.CapabilityError = true;
                }
                _index = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], []);
            }
        }
        finally { _gate.Release(); NotifyChanged(); }
    }

    private Index BuildIndex()
    {
        Slot[] slots;
        // A package marked for a policy unload is left out: reading its capabilities would run its code.
        lock (_sync) slots = _slots.Values.Where(slot => slot.Package is not null && !slot.Stopping).ToArray();
        var transcription = new Dictionary<string, TranscriptionRole>(StringComparer.OrdinalIgnoreCase);
        var llm = new Dictionary<string, LlmRole>(StringComparer.OrdinalIgnoreCase);
        var transcriptionSnapshots = new List<PortableTranscriptionProvider>();
        var llmSnapshots = new List<PortableLlmProvider>();
        var ttsSnapshots = new List<PortableTtsProvider>();
        var postProcessors = new Dictionary<string, PostProcessorRole>(StringComparer.Ordinal);
        var actions = new Dictionary<string, ActionRole>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var plugin = slot.Package!.Plugin;
            if (plugin is ITtsProviderPlugin tts && tts.SupportsPlaybackSelection)
                ttsSnapshots.Add(new(slot.Id, tts.ProviderDisplayName, tts.IsConfigured, Array.AsReadOnly(tts.AvailableVoices.ToArray())) { IsLocal = slot.IsLocal });
            if (plugin is IActionPlugin action)
            {
                if (action.PluginId != slot.Id || string.IsNullOrWhiteSpace(action.ActionId))
                    throw new CapabilityCollisionException("Action identity is invalid or does not match its package.");
                actions.Add(slot.Id, new(slot, action, new(slot.Id, action.ActionId, action.ActionName, action.PluginVersion, slot.Generation)));
            }
            if (plugin is IPostProcessorPlugin processor)
            {
                if (processor.PluginId != slot.Id)
                    throw new CapabilityCollisionException("Text processor identity does not match its package.");
                var snapshot = new PortablePostProcessor(slot.Id, processor.ProcessorName,
                    processor.PluginVersion, processor.Priority, slot.Generation);
                postProcessors.Add(slot.Id, new(slot, processor, snapshot));
            }
            slot.ApiKeyConfigured = plugin is IApiKeyPlugin { IsConfigured: true };
            var engines = (plugin is ITranscriptionEnginePlugin direct ? new[] { direct } : [])
                .Concat(plugin is IAdditionalTranscriptionEnginesProvider additional ? additional.AdditionalTranscriptionEngines : [])
                .Distinct(ReferenceEqualityComparer.Instance).Cast<ITranscriptionEnginePlugin>();
            foreach (var engine in engines.Select(candidate => Isolate(slot, candidate)))
            {
                var id = engine.GetTranscriptionSelectionId();
                if (string.IsNullOrWhiteSpace(id) || engine.PluginId != slot.Id || !transcription.TryAdd(id, new(slot, engine)))
                    throw new CapabilityCollisionException("Transcription capability identity collision or invalid owner: " + id);
                transcriptionSnapshots.Add(new(slot.Id, id, engine.ProviderDisplayName, engine.IsConfigured,
                    engine.SelectedModelId, Array.AsReadOnly(engine.TranscriptionModels.ToArray()), engine.SupportsTranslation, engine is IPcmTranscriptionEnginePlugin,
                    Array.AsReadOnly(engine.SupportedLanguages.ToArray()), engine.ProviderId, engine.SupportsLanguageHints)
                    { ModelStates = CaptureModelStates(slot, id, engine), SupportsLocalLivePreview = engine.SupportsLocalLivePreview, SupportsStreaming = engine.SupportsStreaming && engine.SupportsStreamingCompletion, Locality = slot.Locality });
            }
            var providers = (plugin is ILlmProviderPlugin directLlm ? new[] { directLlm } : [])
                .Concat(plugin is IAdditionalLlmProvidersProvider additionalLlm ? additionalLlm.AdditionalLlmProviders : [])
                .Distinct(ReferenceEqualityComparer.Instance).Cast<ILlmProviderPlugin>();
            foreach (var provider in providers)
            {
                var id = provider.GetLlmSelectionId();
                if (string.IsNullOrWhiteSpace(id) || provider.PluginId != slot.Id || !llm.TryAdd(id, new(slot, provider)))
                    throw new CapabilityCollisionException("LLM capability identity collision or invalid owner: " + id);
                // A remembered local model loads on the next request, so its provider stays selectable while released.
                var restorable = RestorableModel(slot, provider);
                llmSnapshots.Add(new(slot.Id, id, provider.ProviderName, provider.IsAvailable || restorable is not null,
                    Array.AsReadOnly(restorable is null ? provider.SupportedModels.ToArray() : [restorable]))
                    { IsLocal = slot.IsLocal, Locality = slot.Locality });
            }
        }
        return new(transcription, llm, transcriptionSnapshots.ToArray(), llmSnapshots.ToArray()) { PostProcessors = postProcessors, Actions = actions, TtsSnapshots = ttsSnapshots.ToArray() };
    }

    private void Publish(Index index) { lock (_sync) _index = index; }

    // Callers hold _gate. The adapter is created once per activation so model state identities stay stable.
    private ITranscriptionEnginePlugin Isolate(Slot slot, ITranscriptionEnginePlugin engine)
    {
        lock (slot.Isolated) if (slot.Isolated.TryGetValue(engine, out var existing)) return existing;
        if (TranscriptionIsolation is not { } isolation || slot.Directory is null
            || isolation.TryIsolate(engine, slot.Directory, slot.Services) is not { } isolated) return engine;
        lock (slot.Isolated) slot.Isolated.Add(engine, isolated);
        return isolated;
    }

    // Ends the package's workers before its in-process instances are deactivated.
    private static async Task ReleasePackageAsync(Slot slot, PortablePluginPackage package)
    {
        slot.LocalLlmIdle?.Dispose();
        slot.LocalLlmIdle = null;
        IsolatedTranscriptionEngine[] isolated;
        lock (slot.Isolated) { isolated = slot.Isolated.Values.ToArray(); slot.Isolated.Clear(); }
        try
        {
            foreach (var engine in isolated)
                try { await engine.DisposeAsync(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        finally { await package.DisposeAsync(); }
    }
    private sealed class CapabilityCollisionException(string message) : Exception(message);
    private void QueueRefresh()
    {
        lock (_sync) if (_disposed) return;
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { while (Interlocked.Exchange(ref _refreshRequested, 0) != 0) await RefreshCapabilitiesAsync(); }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
                if (Volatile.Read(ref _refreshRequested) != 0) QueueRefresh();
            }
        });
    }
    private static void CancelRequest(Slot slot)
    {
        if (slot.Request is { IsCancellationRequested: false } request)
            slot.CancellationCallbacks = ObserveCancellationAsync(request.CancelAsync());
    }
    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void NotifyChanged()
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((Action)subscriber)(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>Stops new calls, cancels and drains existing calls, then releases all packages once.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var slot in _slots.Values) { slot.Accepting = false; ++slot.Generation; CancelRequest(slot); }
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var slot in _slots.Values)
            {
                var package = slot.Package; slot.Package = null;
                if (package is not null)
                    try { await ReleasePackageAsync(slot, package); } catch (Exception ex) when (ex is not OutOfMemoryException) { slot.Error = "Plugin shutdown failed: " + ex.GetType().Name; }
            }
            Publish(new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], []));
        }
        finally { _gate.Release(); NotifyChanged(); }
    }
}
