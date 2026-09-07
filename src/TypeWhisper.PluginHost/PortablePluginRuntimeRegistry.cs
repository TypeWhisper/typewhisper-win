using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>One installed package's runtime state. Enablement is separate from provider readiness.</summary>
public sealed record PortablePluginRuntimeState(string PluginId, bool Enabled, string? Error, bool HasApiKeySettings = false);
/// <summary>A transcription role's UI snapshot, without exposing its package lifetime.</summary>
public sealed record PortableTranscriptionProvider(string PluginId, string SelectionId, string Name,
    bool Ready, string? SelectedModelId, IReadOnlyList<PluginModelInfo> Models, bool SupportsTranslation, bool SupportsPcm,
    IReadOnlyList<string>? SupportedLanguages = null, string? EngineId = null);
/// <summary>An LLM role's UI snapshot, without exposing its package lifetime.</summary>
public sealed record PortableLlmProvider(string PluginId, string SelectionId, string Name,
    bool Ready, IReadOnlyList<PluginModelInfo> Models);

/// <summary>
/// Owns each activated package once and serializes all capability/configuration calls.
/// The host initializes package storage first and must route runtime calls through this owner.
/// </summary>
public sealed class PortablePluginRuntimeRegistry(PortablePluginStore store, Version hostVersion,
    Func<string, IPluginHostServices> createServices, Func<string, bool>? ownsPlugin = null) : IAsyncDisposable
{
    private sealed class Slot(string id, IPluginHostServices services)
    {
        internal readonly string Id = id;
        internal readonly IPluginHostServices Services = services;
        internal PortablePluginPackage? Package;
        internal bool Accepting;
        internal long Generation;
        internal string? Error;
        internal bool CapabilityError;
        internal CancellationTokenSource? Request;
        internal Task CancellationCallbacks = Task.CompletedTask;
    }
    private sealed record TranscriptionRole(Slot Owner, ITranscriptionEnginePlugin Engine);
    private sealed record LlmRole(Slot Owner, ILlmProviderPlugin Provider);
    private sealed record Index(Dictionary<string, TranscriptionRole> Transcription, Dictionary<string, LlmRole> Llm,
        PortableTranscriptionProvider[] TranscriptionSnapshots, PortableLlmProvider[] LlmSnapshots);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private Index _index = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], []);
    private bool _disposed;
    private int _refreshRequested;
    private int _refreshRunning;

    /// <summary>Raised after state changes; UI subscribers must dispatch to their UI thread.</summary>
    public event Action? Changed;
    /// <summary>Whether a serialized runtime operation is currently executing.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Returns the last published metadata snapshots of enabled transcription roles.</summary>
    public IReadOnlyList<PortableTranscriptionProvider> TranscriptionProviders
    {
        get { lock (_sync) return _index.TranscriptionSnapshots.Where(item => _slots[item.PluginId].Accepting).ToArray(); }
    }
    /// <summary>Returns the last published metadata snapshots of enabled LLM roles.</summary>
    public IReadOnlyList<PortableLlmProvider> LlmProviders
    {
        get { lock (_sync) return _index.LlmSnapshots.Where(item => _slots[item.PluginId].Accepting).ToArray(); }
    }
    /// <summary>Returns known installed packages and their visible activation/capability errors.</summary>
    public IReadOnlyList<PortablePluginRuntimeState> Snapshot()
    {
        lock (_sync) return _slots.Values.Select(slot => new PortablePluginRuntimeState(slot.Id, slot.Accepting, slot.Error, slot.Package?.Plugin is IApiKeyPlugin)).ToArray();
    }

    /// <summary>Restores only explicit saved enablement. Missing preferences never activate a package.</summary>
    public async Task InitializeAsync()
    {
        if (!store.Initialized) throw new InvalidOperationException("Initialize plugin storage before the runtime.");
        foreach (var package in store.Inventory())
        {
            if (package.Manifest is not { } manifest) continue;
            if (ownsPlugin?.Invoke(manifest.Id) == false) continue;
            Slot slot;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_slots.TryGetValue(manifest.Id, out slot!))
                    _slots.Add(manifest.Id, slot = new(manifest.Id, new RuntimePluginHostServices(createServices(manifest.Id), QueueRefresh)));
                slot.Error = package.Error;
            }
            if (package.Error is not null) continue;
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
                finally { if (old is not null) await old.DisposeAsync(); }
                lock (_sync) slot.Error = null;
                return null;
            }
            var directory = store.Resolve(pluginId); // Inventory paths are display identities, not loadable directories.
            var inspection = PortablePluginInventory.Inspect(directory, hostVersion);
            if (inspection.Error is { } invalid) throw new InvalidDataException(invalid);
            if (inspection.Manifest?.Id != pluginId) throw new InvalidDataException("Installed package identity does not match its registration.");
            var loadedHere = slot.Package is null;
            var package = slot.Package ?? await PortablePluginPackage.LoadAsync(directory, slot.Services, hostVersion);
            slot.Package = package;
            try
            {
                var next = BuildIndex();
                lock (_sync)
                {
                    if (_disposed || slot.Generation != generation) throw new OperationCanceledException("Plugin activation was superseded.");
                    slot.Services.SetSetting("Enabled", true);
                    slot.Accepting = true;
                    slot.Error = null;
                    _index = next;
                }
            }
            catch
            {
                slot.Package = null;
                if (loadedHere) await package.DisposeAsync();
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
        return UseAsync(owner, token => _index.Transcription.TryGetValue(selectionId, out var current) && current.Owner == owner
            ? use(current.Engine, token) : throw new InvalidOperationException("Transcription provider changed."), cancellationToken);
    }

    /// <summary>Uses an LLM role within its owning package's serialized request lifetime.</summary>
    public Task<T> UseLlmAsync<T>(string selectionId,
        Func<ILlmProviderPlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_index.Llm.TryGetValue(selectionId, out var role)) throw new InvalidOperationException("LLM provider is unavailable.");
            owner = role.Owner;
        }
        return UseAsync(owner, token => _index.Llm.TryGetValue(selectionId, out var current) && current.Owner == owner
            ? use(current.Provider, token) : throw new InvalidOperationException("LLM provider changed."), cancellationToken);
    }

    /// <summary>
    /// Serializes host-rendered configuration with requests, including IApiKeyPlugin operations.
    /// The callback must not retain the plugin reference or activate/dispose the plugin itself.
    /// </summary>
    public Task<T> UseConfigurationAsync<T>(string pluginId,
        Func<ITypeWhisperPlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_slots.TryGetValue(pluginId, out var slot) || slot.Package is null) throw new InvalidOperationException("Enable this plugin first.");
            owner = slot;
        }
        return UseAsync(owner, token => use(owner.Package!.Plugin, token), cancellationToken);
    }

    private async Task<T> UseAsync<T>(Slot slot, Func<CancellationToken, Task<T>> use, CancellationToken cancellationToken)
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
            request.Token.ThrowIfCancellationRequested();
            lock (_sync)
                if (_disposed || !slot.Accepting || generation != slot.Generation) throw new OperationCanceledException("Plugin selection changed.");
            return result;
        }
        finally
        {
            Task callbacks;
            lock (_sync) { slot.Request = null; callbacks = slot.CancellationCallbacks; slot.CancellationCallbacks = Task.CompletedTask; }
            await callbacks.ConfigureAwait(false);
            request?.Dispose();
            _gate.Release();
            QueueRefresh();
        }
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
        lock (_sync) slots = _slots.Values.Where(slot => slot.Package is not null).ToArray();
        var transcription = new Dictionary<string, TranscriptionRole>(StringComparer.OrdinalIgnoreCase);
        var llm = new Dictionary<string, LlmRole>(StringComparer.OrdinalIgnoreCase);
        var transcriptionSnapshots = new List<PortableTranscriptionProvider>();
        var llmSnapshots = new List<PortableLlmProvider>();
        foreach (var slot in slots)
        {
            var plugin = slot.Package!.Plugin;
            var engines = (plugin is ITranscriptionEnginePlugin direct ? new[] { direct } : [])
                .Concat(plugin is IAdditionalTranscriptionEnginesProvider additional ? additional.AdditionalTranscriptionEngines : [])
                .Distinct(ReferenceEqualityComparer.Instance).Cast<ITranscriptionEnginePlugin>();
            foreach (var engine in engines)
            {
                var id = engine.GetTranscriptionSelectionId();
                if (string.IsNullOrWhiteSpace(id) || engine.PluginId != slot.Id || !transcription.TryAdd(id, new(slot, engine)))
                    throw new CapabilityCollisionException("Transcription capability identity collision or invalid owner: " + id);
                transcriptionSnapshots.Add(new(slot.Id, id, engine.ProviderDisplayName, engine.IsConfigured,
                    engine.SelectedModelId, Array.AsReadOnly(engine.TranscriptionModels.ToArray()), engine.SupportsTranslation, engine is IPcmTranscriptionEnginePlugin,
                    Array.AsReadOnly(engine.SupportedLanguages.ToArray()), engine.ProviderId));
            }
            var providers = (plugin is ILlmProviderPlugin directLlm ? new[] { directLlm } : [])
                .Concat(plugin is IAdditionalLlmProvidersProvider additionalLlm ? additionalLlm.AdditionalLlmProviders : [])
                .Distinct(ReferenceEqualityComparer.Instance).Cast<ILlmProviderPlugin>();
            foreach (var provider in providers)
            {
                var id = provider.GetLlmSelectionId();
                if (string.IsNullOrWhiteSpace(id) || provider.PluginId != slot.Id || !llm.TryAdd(id, new(slot, provider)))
                    throw new CapabilityCollisionException("LLM capability identity collision or invalid owner: " + id);
                llmSnapshots.Add(new(slot.Id, id, provider.ProviderName, provider.IsAvailable, Array.AsReadOnly(provider.SupportedModels.ToArray())));
            }
        }
        return new(transcription, llm, transcriptionSnapshots.ToArray(), llmSnapshots.ToArray());
    }

    private void Publish(Index index) { lock (_sync) _index = index; }
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
                    try { await package.DisposeAsync(); } catch (Exception ex) when (ex is not OutOfMemoryException) { slot.Error = "Plugin shutdown failed: " + ex.GetType().Name; }
            }
            Publish(new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], []));
        }
        finally { _gate.Release(); NotifyChanged(); }
    }
}
