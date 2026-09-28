using System.Runtime.InteropServices;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Recognition is paused after repeated worker crashes; an explicit model load retries at once.</summary>
public sealed class TranscriptionWorkerFaultedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Presents a local engine to the host while its model loading and inference run in a worker process.
/// Metadata, downloads, removal and settings stay with the in-process plugin instance, which never loads
/// a model. A crashed worker is restarted and the request retried; repeated crashes on an accelerated
/// backend switch the engine to its CPU variant, and further crashes pause it instead of looping.
/// </summary>
public sealed class IsolatedTranscriptionEngine : IPcmTranscriptionEnginePlugin, ITranscriptionEngineSelectionIdentity, IAsyncDisposable
{
    internal const int CrashesBeforeCpuFallback = 2;
    internal const int CrashesBeforePause = 3;
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan PauseAfterRepeatedCrashes = TimeSpan.FromSeconds(30);
    private readonly ITranscriptionEnginePlugin _inner;
    private readonly Func<TranscriptionAccelerationPreference, CancellationToken, Task<ITranscriptionWorkerConnection>> _start;
    private readonly Action<PluginLogLevel, string> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITranscriptionWorkerConnection? _worker;
    private TranscriptionWorkerState? _state;
    private int _consecutiveCrashes;
    private bool _cpuFallback;
    private TranscriptionAccelerationPreference _fallbackFrom;
    private DateTimeOffset _pausedUntil;
    private bool _disposed;

    /// <summary>Raised when the engine changed how it runs in a way the user should know about.</summary>
    public event Action<string>? Notice;

    internal IsolatedTranscriptionEngine(ITranscriptionEnginePlugin inner,
        Func<TranscriptionAccelerationPreference, CancellationToken, Task<ITranscriptionWorkerConnection>> start,
        Action<PluginLogLevel, string> log, TimeProvider? time = null)
    {
        _inner = inner; _start = start; _log = log; _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether crashes switched this engine to the CPU until its acceleration preference changes.</summary>
    public bool UsesCpuFallback => FallbackActive();
    /// <summary>How the worker actually runs the engine, or null before a worker has started.</summary>
    public TranscriptionAccelerationStatus? ReportedAccelerationStatus => FallbackActive() || _state is not null ? AccelerationStatus : null;
    /// <summary>The process id of the running worker, or null when none runs.</summary>
    public int? WorkerProcessId => _worker is { IsAlive: true } worker ? worker.ProcessId : null;

    public string PluginId => _inner.PluginId;
    public string PluginName => _inner.PluginName;
    public string PluginVersion => _inner.PluginVersion;
    public string TranscriptionSelectionId => _inner.GetTranscriptionSelectionId();
    public string ProviderId => _inner.ProviderId;
    public int MaximumAudioUploadBytes => _inner.MaximumAudioUploadBytes;
    public string ProviderDisplayName => _inner.ProviderDisplayName;
    public bool IsConfigured => _inner.IsConfigured;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => _inner.TranscriptionModels;
    public string? SelectedModelId => _inner.SelectedModelId;
    public bool SupportsTranslation => _inner.SupportsTranslation;
    public bool SupportsLanguageHints => _inner.SupportsLanguageHints;
    public bool SupportsModelDownload => _inner.SupportsModelDownload;
    public bool SupportsModelRemoval => _inner.SupportsModelRemoval;
    public bool SupportsLocalLivePreview => _inner.SupportsLocalLivePreview;
    public bool SupportsDictionaryTerms => _inner.SupportsDictionaryTerms;
    public DictionaryTermsBudget DictionaryTermsBudget => _inner.DictionaryTermsBudget;
    public bool SupportsStructuredDictionaryTerms => _inner.SupportsStructuredDictionaryTerms;
    public IReadOnlyList<string> SupportedLanguages => _inner.SupportedLanguages;
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends => _inner.SupportedAccelerationBackends;
    public TranscriptionAccelerationPreference AccelerationPreference => _inner.AccelerationPreference;
    public TranscriptionAccelerationStatus AccelerationStatus => FallbackActive()
        ? new(TranscriptionAccelerationBackend.Cpu, "Using CPU",
            "The graphics card engine stopped unexpectedly, so TypeWhisper switched to the CPU. Change the acceleration setting to try the graphics card again.")
        : _state?.Status ?? _inner.AccelerationStatus;

    // The wrapper is created for an already activated package; activation belongs to the package.
    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
    public async Task DeactivateAsync() => await StopWorkerAsync().ConfigureAwait(false);
    public void SelectModel(string modelId) => _inner.SelectModel(modelId);
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference) => _inner.SetAccelerationPreference(preference);
    public bool IsModelDownloaded(string modelId) => _inner.IsModelDownloaded(modelId);
    public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct) => _inner.DownloadModelAsync(modelId, progress, ct);

    public async Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        // The worker may hold the model's files open.
        await StopWorkerAsync().ConfigureAwait(false);
        await _inner.RemoveModelAsync(modelId, ct).ConfigureAwait(false);
    }

    /// <summary>Loads the model in the worker. An explicit load also ends a crash pause.</summary>
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var response = await RunAsync(new() { Command = TranscriptionWorkerCommands.Load, ModelId = modelId }, default, ct, explicitLoad: true).ConfigureAwait(false);
        // Some engines select the model they load. Mirror that on the in-process instance, whose
        // selection drives metadata such as translation support and the next request's model.
        if (response.State?.SelectedModelId == modelId && _inner.SelectedModelId != modelId) _inner.SelectModel(modelId);
    }

    /// <summary>Ends the worker, which releases the model and all native memory.</summary>
    public Task UnloadModelAsync() => StopWorkerAsync();

    public Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples, string? language, bool translate, CancellationToken cancellationToken) =>
        TranscribeCoreAsync(new() { AudioFormat = TranscriptionWorkerAudioFormats.Pcm, Language = language, Translate = translate },
            MemoryMarshal.AsBytes(samples.Span).ToArray(), cancellationToken);

    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
        TranscribeCoreAsync(new() { AudioFormat = TranscriptionWorkerAudioFormats.Wav, Language = language, Translate = translate, Prompt = prompt }, wavAudio, ct);

    public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(byte[] wavAudio, IReadOnlyList<string> languageHints, bool translate, string? prompt, CancellationToken ct) =>
        TranscribeCoreAsync(new() { AudioFormat = TranscriptionWorkerAudioFormats.Wav, LanguageHints = languageHints.ToArray(), Translate = translate, Prompt = prompt }, wavAudio, ct);

    // Progress callbacks cannot cross the process boundary; the final result is unchanged.
    public Task<PluginTranscriptionResult> TranscribeStreamingAsync(byte[] wavAudio, string? language, bool translate, string? prompt,
        Func<string, bool> onProgress, CancellationToken ct) => TranscribeAsync(wavAudio, language, translate, prompt, ct);

    public Task<PluginTranscriptionResult> TranscribeStreamingWithLanguageHintsAsync(byte[] wavAudio, IReadOnlyList<string> languageHints, bool translate,
        string? prompt, Func<string, bool> onProgress, CancellationToken ct) => TranscribeWithLanguageHintsAsync(wavAudio, languageHints, translate, prompt, ct);

    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(TranscriptionWorkerMessage request, byte[] audio, CancellationToken ct)
    {
        var response = await RunAsync(request with { Command = TranscriptionWorkerCommands.Transcribe, ModelId = _inner.SelectedModelId }, audio, ct).ConfigureAwait(false);
        return TranscriptionWorkerProtocol.FromResult(response.Result ?? throw new InvalidDataException("The transcription worker returned no result."));
    }

    private async Task<TranscriptionWorkerMessage> RunAsync(TranscriptionWorkerMessage request, ReadOnlyMemory<byte> payload, CancellationToken ct, bool explicitLoad = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (explicitLoad) { _pausedUntil = default; _consecutiveCrashes = 0; }
            if (_time.GetUtcNow() < _pausedUntil)
                throw new TranscriptionWorkerFaultedException(PausedMessage());
            var runtimeRestarted = false;
            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var acceleration = EffectivePreference();
                TranscriptionWorkerMessage response;
                try
                {
                    var worker = await EnsureWorkerAsync(ct).ConfigureAwait(false);
                    response = await worker.SendAsync(request, payload, ct).ConfigureAwait(false);
                }
                catch (TranscriptionWorkerCrashedException crash)
                {
                    await DiscardWorkerAsync().ConfigureAwait(false);
                    RecordCrash(crash, acceleration);
                    ct.ThrowIfCancellationRequested();
                    if (_time.GetUtcNow() < _pausedUntil) throw new TranscriptionWorkerFaultedException(PausedMessage(), crash);
                    if (attempt >= MaximumAttempts)
                        throw new InvalidOperationException(ProviderDisplayName + " stopped unexpectedly again. Your recording is kept; try again in a moment or choose another model.", crash);
                    continue;
                }
                if (response.State is { } state) _state = state;
                if (response.Error is { } error)
                {
                    // An engine that installed a native runtime may need a fresh process to load it. That used
                    // to mean restarting TypeWhisper; here a new worker is enough, once.
                    if (!runtimeRestarted && response.State?.Status.RequiresRestart == true && error.Kind != "canceled")
                    {
                        runtimeRestarted = true;
                        await DiscardWorkerAsync().ConfigureAwait(false);
                        attempt--;
                        continue;
                    }
                    throw TranscriptionWorkerProtocol.ToException(error);
                }
                _consecutiveCrashes = 0;
                return response;
            }
        }
        finally { _gate.Release(); }
    }

    private void RecordCrash(TranscriptionWorkerCrashedException crash, TranscriptionAccelerationPreference acceleration)
    {
        _consecutiveCrashes++;
        _log(PluginLogLevel.Error, $"{ProviderDisplayName} worker crashed ({_consecutiveCrashes} in a row, acceleration {acceleration}): {crash.Message}"
            + (string.IsNullOrWhiteSpace(crash.Diagnostics) ? "" : Environment.NewLine + crash.Diagnostics));
        if (!FallbackActive() && CanFallBackToCpu() && _consecutiveCrashes >= CrashesBeforeCpuFallback)
        {
            _cpuFallback = true;
            _fallbackFrom = _inner.AccelerationPreference;
            _consecutiveCrashes = 0;
            _log(PluginLogLevel.Warning, $"{ProviderDisplayName} switched to the CPU after repeated crashes with {_fallbackFrom} acceleration.");
            RaiseNotice(ProviderDisplayName + " stopped unexpectedly on the graphics card, so TypeWhisper switched it to the CPU. Transcription may be slower.");
        }
        else if (_consecutiveCrashes >= CrashesBeforePause)
        {
            _pausedUntil = _time.GetUtcNow() + PauseAfterRepeatedCrashes;
            _consecutiveCrashes = 0;
            RaiseNotice(PausedMessage());
        }
    }

    private string PausedMessage() => ProviderDisplayName + " stopped unexpectedly several times, so TypeWhisper paused it briefly. " +
        "Your recording is kept. Try again in a minute, select the model again, or choose another model.";

    private void RaiseNotice(string message)
    {
        foreach (var subscriber in Notice?.GetInvocationList() ?? [])
            try { ((Action<string>)subscriber)(message); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private bool CanFallBackToCpu() => EffectivePreference() != TranscriptionAccelerationPreference.Cpu
        && _inner.SupportedAccelerationBackends.Contains(TranscriptionAccelerationBackend.Cpu)
        && _inner.SupportedAccelerationBackends.Any(backend => backend != TranscriptionAccelerationBackend.Cpu);

    // The fallback holds until the user changes the acceleration preference, which is a new explicit choice.
    private bool FallbackActive()
    {
        if (_cpuFallback && _inner.AccelerationPreference != _fallbackFrom) _cpuFallback = false;
        return _cpuFallback;
    }

    private TranscriptionAccelerationPreference EffectivePreference() =>
        FallbackActive() ? TranscriptionAccelerationPreference.Cpu : _inner.AccelerationPreference;

    private async Task<ITranscriptionWorkerConnection> EnsureWorkerAsync(CancellationToken ct)
    {
        var preference = EffectivePreference();
        if (_worker is { IsAlive: true } running && running.Acceleration == preference) return running;
        await DiscardWorkerAsync().ConfigureAwait(false);
        _worker = await _start(preference, ct).ConfigureAwait(false);
        if (_worker.State is { } state) _state = state;
        return _worker;
    }

    private async Task DiscardWorkerAsync()
    {
        var worker = _worker;
        _worker = null;
        if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StopWorkerAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DiscardWorkerAsync().ConfigureAwait(false); _state = null; }
        finally { _gate.Release(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Ends the worker. The in-process plugin belongs to its package and is not disposed here.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await DiscardWorkerAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
