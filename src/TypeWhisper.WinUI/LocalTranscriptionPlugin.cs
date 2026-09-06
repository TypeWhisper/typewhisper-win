using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

internal sealed record LocalTranscriptionLease(IPcmTranscriptionEnginePlugin Engine, IAsyncDisposable Lifetime);
internal sealed record LocalModelState(PluginModelInfo Model, bool Downloaded);

// The dictation owner drains inference before switching models. Downloads may run
// alongside inference; this gate prevents package disposal or a competing model operation.
internal sealed class LocalTranscriptionPlugin : IAsyncDisposable
{
    internal const string PluginId = "com.typewhisper.sherpa-onnx";
    internal const string ModelId = "parakeet-tdt-0.6b";
    private LocalTranscriptionLease? _lease;
    private readonly IPluginHostServices _host;
    private readonly Func<Task<LocalTranscriptionLease>> _load;
    private readonly Func<string> _packageDirectory;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource? _download;
    private bool _disposed;
    internal event Action? Changed;
    internal bool Enabled => _lease is not null;
    internal bool Ready => Enabled && ActiveModelId is not null;
    internal bool Busy { get; private set; }
    internal string? ActiveModelId { get; private set; }
    internal IReadOnlyList<string> SupportedLanguages => _lease?.Engine.SupportedLanguages ?? [];
    internal string Language => SupportedLanguages.Count == 0 ? "auto" :
        SupportedLanguages.Contains(_host.GetSetting<string>("Language") ?? "en") ? _host.GetSetting<string>("Language") ?? "en" : SupportedLanguages[0];
    internal void SelectLanguage(string language)
    {
        if (!Ready || (SupportedLanguages.Count == 0 ? language != "auto" : !SupportedLanguages.Contains(language)))
            throw new ArgumentException("This model does not support that language.");
        _host.SetSetting("Language", language); Changed?.Invoke();
    }
    internal string ActiveModelName => Models.FirstOrDefault(m => m.Model.Id == ActiveModelId)?.Model.DisplayName ?? "No model loaded";
    internal string? DownloadingModelId { get; private set; }
    internal double Progress { get; private set; }
    internal string? Error { get; private set; }
    internal string? Feedback { get; private set; }
    internal IReadOnlyList<LocalModelState> Models
    {
        get
        {
            var engine = _lease?.Engine;
            return engine?.TranscriptionModels.Select(m => new LocalModelState(m, engine.IsModelDownloaded(m.Id))).ToArray() ?? [];
        }
    }

    internal LocalTranscriptionPlugin(IPluginHostServices? host = null, Func<Task<LocalTranscriptionLease>>? load = null, Func<string>? packageDirectory = null)
    {
        _packageDirectory = packageDirectory ?? (() => Path.Combine(AppContext.BaseDirectory, "Plugins", PluginId));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _host = host ?? new VocabularyHostServices(Path.Combine(local, "TypeWhisper-WinUI-DevUserData", "PluginData", PluginId),
            assetDirectory: Path.Combine(local, "TypeWhisper-DevUserData", "PluginData", PluginId));
        _load = load ?? LoadPackageAsync;
    }

    private async Task<LocalTranscriptionLease> LoadPackageAsync()
    {
        var package = await PortablePluginPackage.LoadAsync(_packageDirectory(), _host, LocalCtcVocabulary.HostVersion);
        if (package.Plugin is IPcmTranscriptionEnginePlugin engine) return new(engine, package);
        await package.DisposeAsync();
        throw new NotSupportedException("The local plugin does not provide PCM transcription.");
    }

    internal async Task InitializeAsync()
    {
        if (_host.GetSetting<bool?>("Enabled") != false) await SetEnabledAsync(true);
    }

    internal async Task SetEnabledAsync(bool enabled)
    {
        if (!await _operations.WaitAsync(0)) throw new InvalidOperationException("Finish or cancel the current model operation first.");
        Busy = true; Changed?.Invoke();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled)
            {
                _host.SetSetting("Enabled", false);
                await ReleaseAsync();
                Error = null;
                return;
            }
            if (Enabled) return;
            Error = null; Feedback = null;
            _lease = await _load();
            _lease.Engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
            try { _host.SetSetting("Enabled", true); }
            catch { await ReleaseAsync(); throw; }
            var selected = _host.GetSetting<string>("SelectedModelId") ?? ModelId;
            if (!Models.Any(m => m.Model.Id == selected && m.Downloaded))
            {
                Feedback = "Choose a downloaded model or download one in Models.";
                return;
            }
            // An activation failure keeps the package available for download/retry.
            try { await ActivateCoreAsync(selected, CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Error = "Could not load model: " + ex.Message; }
        }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    internal async Task ActivateAsync(string modelId, CancellationToken ct = default)
    {
        if (!await _operations.WaitAsync(0, ct)) throw new InvalidOperationException("A model operation is already in progress.");
        Busy = true; Error = null; Feedback = null; Changed?.Invoke();
        try { ObjectDisposedException.ThrowIf(_disposed, this); await ActivateCoreAsync(modelId, ct); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = "Could not load model: " + ex.Message; throw; }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    private async Task ActivateCoreAsync(string modelId, CancellationToken ct)
    {
        var engine = _lease?.Engine ?? throw new InvalidOperationException("Enable the local plugin in Plugins first.");
        if (!Models.Any(m => m.Model.Id == modelId && m.Downloaded)) throw new InvalidOperationException("Download this model before selecting it.");
        if (ActiveModelId == modelId) return;
        var previous = ActiveModelId;
        ActiveModelId = null;
        try
        {
            await engine.LoadModelAsync(modelId, ct);
            ct.ThrowIfCancellationRequested();
            _host.SetSetting("SelectedModelId", modelId);
            ActiveModelId = modelId;
            Feedback = "Model ready for dictation.";
        }
        catch
        {
            try
            {
                if (previous is not null) { await engine.LoadModelAsync(previous, CancellationToken.None); ActiveModelId = previous; }
                else await engine.UnloadModelAsync();
            }
            catch (Exception rollback) when (rollback is not OutOfMemoryException)
            { _host.Log(PluginLogLevel.Error, "Could not restore previous model: " + rollback.Message); }
            throw;
        }
    }

    internal async Task DownloadAsync(string modelId)
    {
        if (!await _operations.WaitAsync(0)) throw new InvalidOperationException("A model operation is already in progress.");
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        Busy = true; Progress = 0; Error = null; Feedback = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var engine = _lease?.Engine ?? throw new InvalidOperationException("Enable the local plugin in Plugins first.");
            if (!engine.TranscriptionModels.Any(m => m.Id == modelId)) throw new ArgumentException("Unknown model.");
            if (!engine.SupportsModelDownload) throw new NotSupportedException("This plugin does not support downloads.");
            DownloadingModelId = modelId; Changed?.Invoke();
            await engine.DownloadModelAsync(modelId, new InlineProgress(value =>
            {
                if (!ReferenceEquals(_download, cancellation) || !double.IsFinite(value)) return;
                Progress = Math.Max(Progress, Math.Clamp(value, 0, 1)); Changed?.Invoke();
            }), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!engine.IsModelDownloaded(modelId)) throw new IOException("The download did not produce a complete model.");
            Feedback = "Download complete. Choose Use model to activate it.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Feedback = "Download canceled. Your active model is unchanged."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = "Download failed: " + ex.Message; }
        finally { _download = null; DownloadingModelId = null; Busy = false; _operations.Release(); Changed?.Invoke(); }
    }
    internal void CancelDownload() => _download?.Cancel();
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }

    internal async Task<(string Text, VocabularyTokenTiming[] Timings)> DecodeAsync(float[] samples, bool includeTimings)
    {
        if (!Ready) throw new InvalidOperationException("Choose and load a model before dictating.");
        var result = await _lease!.Engine.TranscribePcmAsync(samples, Language == "auto" ? null : Language, false, CancellationToken.None);
        return (result.Text, includeTimings ? result.TokenTimings.ToArray() : []);
    }

    private async Task ReleaseAsync()
    {
        ActiveModelId = null;
        var lease = _lease; _lease = null;
        if (lease is not null) await lease.Lifetime.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        CancelDownload();
        await _operations.WaitAsync();
        try { await ReleaseAsync(); }
        finally { _operations.Release(); }
    }
}
