using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

// Owns the model lifetime and serializes all inference/enable/disable operations.
// The factory loads only a package that the caller has explicitly enabled.
public sealed class VocabularyPluginSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly VocabularyPipeline _pipeline = new();
    private readonly Func<Task<IVocabularyPluginLease>> _load;
    private IVocabularyPluginLease? _lease;
    private CancellationTokenSource? _request;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private bool _enabled;
    private bool _disposed;
    private long _generation;

    public VocabularyPluginSession(Func<Task<IVocabularyPluginLease>> load) => _load = load;
    public bool Enabled { get { lock (_sync) return _enabled; } }

    public async Task SetEnabledAsync(bool enabled)
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
        }
        await _gate.WaitAsync();
        try
        {
            lock (_sync) { if (_disposed || generation != _generation) return; }
            if (_lease is not null)
            {
                var old = _lease; _lease = null;
                await old.DisposeAsync();
            }
            if (!enabled) return;
            var loaded = await _load();
            bool accept;
            lock (_sync)
            {
                accept = !_disposed && generation == _generation;
                if (accept) { _lease = loaded; _enabled = true; }
            }
            if (!accept) await loaded.DisposeAsync();
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
        await _gate.WaitAsync(cancellation);
        CancellationTokenSource? request = null;
        try
        {
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
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true; _enabled = false; ++_generation;
            CancelRequest();
        }
        await _gate.WaitAsync();
        try
        {
            var lease = _lease; _lease = null;
            if (lease is not null) await lease.DisposeAsync();
        }
        finally { _gate.Release(); }
    }

    // Cancellation callbacks are plugin code and may throw. They cannot stop
    // teardown or make an obsolete request publish its result.
    private void CancelRequest()
    {
        if (_request is { IsCancellationRequested: false } request)
            _cancellationCallbacks = ObserveCancellationAsync(request.CancelAsync());
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

    public static async Task<IVocabularyPluginLease> LoadAsync(string directory, IPluginHostServices services, Version hostVersion)
    {
        var package = await PortablePluginPackage.LoadAsync(directory, services, hostVersion);
        if (package.Plugin is IVocabularyRescorerPlugin rescorer) return new VocabularyPluginLease(package, rescorer);
        await package.DisposeAsync();
        throw new InvalidDataException("This package does not expose acoustic vocabulary rescoring.");
    }
    public ValueTask DisposeAsync() => _package.DisposeAsync();
}
