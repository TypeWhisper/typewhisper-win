using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>
/// Owns the model lifetime and serializes all inference, enable and disable operations. The factory loads
/// only a package that the caller has explicitly enabled. With an idle policy, an enabled session releases
/// its model after the idle delay; the next rescoring loads it again, or <see cref="PrepareAsync"/> does
/// so earlier, while the user still speaks.
/// </summary>
public sealed class VocabularyPluginSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly VocabularyPipeline _pipeline = new();
    private readonly Func<CancellationToken, Task<IVocabularyPluginLease>> _load;
    private readonly ModelIdleTimer? _idle;
    private IVocabularyPluginLease? _lease;
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _activation;
    private Task _activationCallbacks = Task.CompletedTask;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private bool _enabled;
    private bool _disposed;
    private long _generation;

    /// <summary>Creates a disabled session; without a policy the model stays loaded until it is disabled.</summary>
    public VocabularyPluginSession(Func<Task<IVocabularyPluginLease>> load, ModelIdleUnloadPolicy? idlePolicy = null) : this(_ => load(), idlePolicy) { }
    /// <summary>Creates a disabled session whose loads observe cancellation by a later enable, disable or disposal.</summary>
    public VocabularyPluginSession(Func<CancellationToken, Task<IVocabularyPluginLease>> load, ModelIdleUnloadPolicy? idlePolicy = null)
    {
        _load = load;
        if (idlePolicy is not null) _idle = new(idlePolicy, ReleaseIdleAsync);
    }
    /// <summary>Whether rescoring is enabled. Stays true while an idle model is released.</summary>
    public bool Enabled { get { lock (_sync) return _enabled; } }
    /// <summary>Whether the model is in memory: false while disabled and after an idle release.</summary>
    public bool Loaded { get { lock (_sync) return _lease is not null; } }
    /// <summary>Raised off the UI thread after the model was loaded or released.</summary>
    public event Action? Changed;
    /// <summary>Raised off the UI thread when a released model could not load again; that rescoring keeps the original text.</summary>
    public event Action<Exception>? LoadFailed;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            // Immediately prevent new inference and publication, even while a
            // previous activation or native request is still draining.
            _enabled = false;
            CancelRequest();
            CancelActivation();
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync) { if (_disposed || generation != _generation) return; }
            _idle?.Cancel();
            if (await ReleaseLeaseUnsafeAsync()) RaiseChanged();
            if (enabled) await LoadUnsafeAsync(generation, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public void RequestCancelActivation()
    {
        lock (_sync) { _enabled = false; ++_generation; CancelActivation(); }
    }

    /// <summary>
    /// Loads a model released after inactivity before it is needed, for example when a recording begins.
    /// A loaded or disabled session is left unchanged. A failed load is reported through <see cref="LoadFailed"/>
    /// instead of thrown; the next rescoring tries again.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_sync)
        {
            if (_disposed || !_enabled || _lease is not null) return;
            generation = _generation;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync) { if (_disposed || !_enabled || generation != _generation || _lease is not null) return; }
            try { await LoadUnsafeAsync(generation, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { RaiseLoadFailed(ex); }
        }
        finally { _gate.Release(); }
    }

    public async Task<VocabularyOutcome> RefineAsync(Guid recordingId, string text, float[] audio, int sampleRate,
        IReadOnlyList<VocabularyTokenTiming> timings, IReadOnlyList<VocabularyTermHint> terms, CancellationToken cancellation = default)
    {
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_enabled) return new(text, false);
            generation = _generation;
        }
        // A load that a recording started holds the gate: this waits for it rather than skipping the terms.
        await _gate.WaitAsync(cancellation);
        CancellationTokenSource? request = null;
        try
        {
            if (!Loaded)
            {
                // Nothing prepared the released model, for example for a file transcription: load it now.
                try { if (!await LoadUnsafeAsync(generation, cancellation)) return new(text, false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RaiseLoadFailed(ex);
                    // Like the pipeline, diagnostics carry the failure type only, never plugin error text.
                    return new(text, false, "Vocabulary model load failed: " + ex.GetType().Name);
                }
            }
            IVocabularyRescorerPlugin plugin;
            lock (_sync)
            {
                if (_disposed || !_enabled || generation != _generation || _lease is null) return new(text, false);
                plugin = _lease.Plugin;
                request = _request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            }
            var result = await _pipeline.RefineAsync(plugin, recordingId, text, audio, sampleRate, timings, terms, request.Token);
            lock (_sync)
                return _disposed || !_enabled || generation != _generation ? new(text, false) : result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new(text, false); } // Disabled/unloaded, preserve the original final transcript.
        finally
        {
            Task callbacks = Task.CompletedTask;
            lock (_sync)
            {
                if (ReferenceEquals(_request, request))
                { _request = null; callbacks = _cancellationCallbacks; _cancellationCallbacks = Task.CompletedTask; }
            }
            await callbacks;
            request?.Dispose();
            // Each use restarts the idle wait of the model that stays loaded for the next dictation.
            if (Loaded) _idle?.Touch();
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true; _enabled = false; ++_generation;
            CancelRequest();
            CancelActivation();
        }
        await _gate.WaitAsync();
        try
        {
            _idle?.Dispose();
            await ReleaseLeaseUnsafeAsync();
        }
        finally { _gate.Release(); }
    }

    // Callers hold _gate. Returns false when a newer enable, disable or disposal superseded this load;
    // a failed load propagates to the caller.
    private async Task<bool> LoadUnsafeAsync(long generation, CancellationToken cancellationToken)
    {
        CancellationTokenSource activation;
        lock (_sync)
        {
            if (_disposed || generation != _generation) return false;
            activation = _activation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        try
        {
            var loaded = await _load(activation.Token);
            bool accept;
            lock (_sync)
            {
                accept = !_disposed && generation == _generation && !activation.IsCancellationRequested;
                if (accept) { _lease = loaded; _enabled = true; }
            }
            if (!accept) { await loaded.DisposeAsync(); return false; }
            _idle?.Touch();
            RaiseChanged();
            return true;
        }
        catch (OperationCanceledException) when (activation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return false; }
        finally
        {
            Task callbacks = Task.CompletedTask;
            lock (_sync)
            {
                if (ReferenceEquals(_activation, activation))
                { _activation = null; callbacks = _activationCallbacks; _activationCallbacks = Task.CompletedTask; }
            }
            await callbacks;
            activation.Dispose();
        }
    }

    // Callers hold _gate. Disposing the lease deactivates the plugin, which frees the native model memory.
    private async Task<bool> ReleaseLeaseUnsafeAsync()
    {
        IVocabularyPluginLease? lease;
        lock (_sync) { lease = _lease; _lease = null; }
        if (lease is null) return false;
        await lease.DisposeAsync();
        return true;
    }

    // Skips the release while a rescoring or load holds the gate; the timer retries it shortly afterwards.
    // The session stays enabled, so the next rescoring loads the model again.
    private async Task<bool> ReleaseIdleAsync()
    {
        if (!_gate.Wait(0)) return false;
        bool released;
        try
        {
            lock (_sync) if (_disposed || !_enabled) return true;
            released = await ReleaseLeaseUnsafeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (released) RaiseChanged();
        return true;
    }

    private void RaiseChanged() => Raise(Changed, subscriber => ((Action)subscriber)());
    private void RaiseLoadFailed(Exception error) => Raise(LoadFailed, subscriber => ((Action<Exception>)subscriber)(error));

    // Subscribers are host code; a failing one must not break the session or its idle timer.
    private static void Raise(Delegate? handlers, Action<Delegate> invoke)
    {
        foreach (var subscriber in handlers?.GetInvocationList() ?? [])
            try { invoke(subscriber); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* See above. */ }
    }

    // Cancellation callbacks are plugin code and may throw. They cannot stop
    // teardown or make an obsolete request publish its result.
    private void CancelRequest()
    {
        if (_request is { IsCancellationRequested: false } request)
            _cancellationCallbacks = ObserveCancellationAsync(request.CancelAsync());
    }

    private void CancelActivation()
    {
        if (_activation is { IsCancellationRequested: false } activation)
            _activationCallbacks = ObserveCancellationAsync(activation.CancelAsync());
    }

    private static async Task ObserveCancellationAsync(Task callbacks)
    {
        try { await callbacks; }
        catch (AggregateException) { }
    }
}

public interface IVocabularyPluginLease : IAsyncDisposable
{
    IVocabularyRescorerPlugin Plugin { get; }
}

public sealed class VocabularyPluginLease : IVocabularyPluginLease
{
    private readonly PortablePluginPackage _package;
    public IVocabularyRescorerPlugin Plugin { get; }
    private VocabularyPluginLease(PortablePluginPackage package, IVocabularyRescorerPlugin plugin)
    { _package = package; Plugin = plugin; }

    public static async Task<IVocabularyPluginLease> LoadAsync(string directory, IPluginHostServices services, Version hostVersion, CancellationToken ct = default)
    {
        var package = await PortablePluginPackage.LoadAsync(directory, services, hostVersion, ct);
        if (package.Plugin is IVocabularyRescorerPlugin rescorer) return new VocabularyPluginLease(package, rescorer);
        await package.DisposeAsync();
        throw new InvalidDataException("This package does not expose acoustic vocabulary rescoring.");
    }
    public ValueTask DisposeAsync() => _package.DisposeAsync();
}
