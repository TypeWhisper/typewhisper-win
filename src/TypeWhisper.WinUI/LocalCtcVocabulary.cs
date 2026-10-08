using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed class LocalCtcVocabulary : IAsyncDisposable
{
    internal const string PluginId = "com.typewhisper.parakeet-ctc";
    internal static readonly Version HostVersion = new(1, 1, 6);
    internal event Action? Changed;
    internal bool Busy { get; private set; }
    internal string? Status { get; private set; }
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly object _activationLock = new();
    private CancellationTokenSource? _pendingActivation;
    private Task _activationCallbacks = Task.CompletedTask;
    private static readonly string DataDirectory = WinUIProfile.DataPath("PluginData", "com.typewhisper.parakeet-ctc");
    private readonly VocabularyDiagnosticLog _diagnostics;
    private readonly VocabularyHostServices _host;
    private readonly VocabularyPluginSession _session;
    internal bool Enabled => _session.Enabled;
    // False after an idle release; the session stays enabled and loads the model again when it is needed.
    internal bool Loaded => _session.Loaded;
    internal string? Error { get; private set; }
    // Raised off the UI thread when a released model could not load again for a rescoring.
    internal event Action<Exception>? LoadFailed;

    internal LocalCtcVocabulary(string? dataDirectory = null, Func<IPluginHostServices, Task<IVocabularyPluginLease>>? load = null, Func<string>? packageDirectory = null,
        ModelIdleUnloadPolicy? idlePolicy = null)
    {
        dataDirectory ??= DataDirectory;
        _diagnostics = new(Path.Combine(dataDirectory, "ctc-diagnostics.jsonl"));
        _host = new(dataDirectory, message =>
        {
            _diagnostics.Write(message);
            if (!Busy) return;
            void Publish() { if (!Busy) return; Status = message; Changed?.Invoke(); }
            if (_uiContext is not null) _uiContext.Post(_ => Publish(), null);
            else Publish();
        });
        _session = new(ct => load is not null ? load(_host) : VocabularyPluginLease.LoadAsync(
            packageDirectory?.Invoke() ?? Path.Combine(AppContext.BaseDirectory, "Plugins", PluginId), _host, HostVersion, ct), idlePolicy);
        // The journal shows whether the model was in memory when a dictation was rescored.
        _session.Changed += () => Trace("model " + (_session.Loaded ? "loaded" : "released"));
        _session.LoadFailed += ex => { Trace("model load-failed error=" + ex.GetType().Name); LoadFailed?.Invoke(ex); };
    }
    internal void Trace(string message) => _diagnostics.Write(message);

    // A model released after inactivity loads while the user speaks instead of at the final rescoring.
    internal void PrepareForDictation()
    {
        if (Enabled && !Loaded) _ = PrepareAsync();
    }

    // Load failures are reported through LoadFailed; the rescoring tries once more and keeps its text otherwise.
    private async Task PrepareAsync()
    {
        try { await _session.PrepareAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace("model prepare-failed error=" + ex.GetType().Name); }
    }

    internal void RequestCancelActivation()
    {
        lock (_activationLock)
            if (_pendingActivation is { IsCancellationRequested: false } activation)
                _activationCallbacks = ObserveCancellationAsync(activation.CancelAsync());
        _session.RequestCancelActivation();
    }

    internal Task<string?> SetEnabledAsync(bool enabled) => SetEnabledAsync(enabled, CancellationToken.None);

    internal async Task<string?> SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (!enabled) RequestCancelActivation();
        if (enabled && !await _settingsGate.WaitAsync(0, ct)) return Loc.T("A plugin operation is already in progress.");
        if (!enabled) await _settingsGate.WaitAsync(ct);
        using var activation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_activationLock) _pendingActivation = activation;
        Busy = true; Error = null; Status = enabled ? Loc.T("Preparing NVIDIA dictionary boosting…") : Loc.T("Stopping dictionary boosting…"); Changed?.Invoke();
        try
        {
            // Enablement is owned by the parent transcription plugin, never a separate preference.
            await Task.Run(() => _session.SetEnabledAsync(enabled, activation.Token));
            activation.Token.ThrowIfCancellationRequested();
            return Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (activation.IsCancellationRequested) { return Error = Loc.T("Dictionary boosting setup was canceled. Choose Retry setup in plugin settings."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _session.SetEnabledAsync(false);
            return Error = Loc.T("Dictionary boosting unavailable: {0} Choose Retry setup to try again.", ex.Message);
        }
        finally
        {
            Task callbacks;
            lock (_activationLock) { _pendingActivation = null; callbacks = _activationCallbacks; _activationCallbacks = Task.CompletedTask; }
            await callbacks;
            _settingsGate.Release(); Busy = false; Status = null; Changed?.Invoke();
        }
    }

    internal async Task<VocabularyOutcome> RefineAsync(Guid recording, string text, float[] audio,
        IReadOnlyList<VocabularyTokenTiming> timings, IReadOnlyList<TypeWhisper.Core.Models.DictionaryEntry> terms, CancellationToken ct = default)
    {
        timings = FitTimingsToAudio(timings, audio.Length / 16000d);
        Trace($"{recording} host-start enabled={Enabled} samples={audio.Length} timings={timings.Count} terms={terms.Count}");
        if (timings.Count == 0 || terms.Count == 0 || audio.Length == 0)
            Trace($"{recording} pipeline-skipped reason={(timings.Count == 0 ? "no-token-timings" : terms.Count == 0 ? "no-terms" : "no-audio")}");
        try
        {
            var result = await _session.RefineAsync(recording, text, audio, 16000, timings, terms.Select(t => new VocabularyTermHint(t.Original, t.CtcMinSimilarity)).ToArray(), ct);
            Trace($"{recording} host-finish modified={result.Modified} error={result.Error ?? "none"}");
            return result;
        }
        // Disabling the optional add-on must not discard an already decoded dictation.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { Trace($"{recording} cancelled"); return new(text, false); }
        catch (ObjectDisposedException) { Trace($"{recording} disposed"); return new(text, false); }
    }
    /// <summary>
    /// The final decode appends silence (ShortClipCapturePolicy.PadForFinalDecode), so the last tokens can end
    /// after the captured audio, which the rescorer rejects. Pulls them back inside it.
    /// </summary>
    internal static IReadOnlyList<VocabularyTokenTiming> FitTimingsToAudio(IReadOnlyList<VocabularyTokenTiming> timings, double audioSeconds)
    {
        const double minimumTokenSeconds = 0.01;
        if (timings.Count == 0 || timings[^1].EndSeconds <= audioSeconds || audioSeconds <= minimumTokenSeconds) return timings;
        return timings.Select(timing =>
        {
            if (timing.EndSeconds <= audioSeconds) return timing;
            return timing with { StartSeconds = Math.Min(timing.StartSeconds, audioSeconds - minimumTokenSeconds), EndSeconds = audioSeconds };
        }).ToArray();
    }
    private static async Task ObserveCancellationAsync(Task callbacks)
    {
        try { await callbacks.ConfigureAwait(false); }
        catch (AggregateException) { }
    }
    public async ValueTask DisposeAsync()
    {
        RequestCancelActivation();
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        finally { _settingsGate.Release(); }
    }
}
