using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed record LocalTranscriptionLease(IPcmTranscriptionEnginePlugin Engine, IAsyncDisposable Lifetime);
internal sealed record LocalModelState(PluginModelInfo Model, bool Downloaded);

// The dictation owner drains inference before switching models. Downloads may run
// alongside inference; this gate prevents package disposal or a competing model operation.
internal sealed class LocalTranscriptionPlugin : IAsyncDisposable
{
    internal const string PluginId = "com.typewhisper.sherpa-onnx";
    internal const string ModelId = "parakeet-tdt-0.6b";
    internal const string AccelerationSetting = "Acceleration";
    // Automatic uses NVIDIA CUDA once its runtime is installed and the CPU otherwise.
    internal static readonly IReadOnlyList<TranscriptionAccelerationPreference> AccelerationChoices =
        [TranscriptionAccelerationPreference.Auto, TranscriptionAccelerationPreference.Cpu, TranscriptionAccelerationPreference.NvidiaCuda];
    private LocalTranscriptionLease? _lease;
    private readonly IPluginHostServices _host;
    private readonly Func<Task<LocalTranscriptionLease>> _load;
    private readonly Func<string> _packageDirectory;
    private readonly TranscriptionIsolation? _isolation;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource? _download;
    private bool _disposed;
    internal event Action? Changed;
    internal bool Enabled => _lease is not null;
    internal bool Ready => Enabled && ActiveModelId is not null;
    internal bool SupportsLocalLivePreview => Ready && _lease?.Engine.SupportsLocalLivePreview == true;
    internal bool SupportsTranslation => Ready && _lease?.Engine.SupportsTranslation == true;
    internal bool Busy { get; private set; }
    internal string? ActiveModelId { get; private set; }
    internal string? SelectedModelId => _lease?.Engine.SelectedModelId;
    internal IReadOnlyList<string> SupportedLanguages => _lease?.Engine.SupportedLanguages ?? [];
    // A model without a language list detects the language itself. Its choice is kept apart from the model language,
    // so a language saved for Canary does not silently apply to Parakeet.
    private string LanguageSetting => SupportedLanguages.Count == 0 ? "TextLanguage" : "Language";
    internal string Language => SpokenLanguageChoices.Resolve(SupportedLanguages, _host.GetSetting<string>(LanguageSetting));
    internal void SelectLanguage(string language)
    {
        if (!Ready || !SpokenLanguageChoices.CanSelect(SupportedLanguages, language))
            throw new ArgumentException(Loc.T("This model does not support that language."));
        _host.SetSetting(LanguageSetting, language); Changed?.Invoke();
    }
    internal string ActiveModelName => Models.FirstOrDefault(m => m.Model.Id == ActiveModelId)?.Model.DisplayName ?? Loc.T("No model loaded");
    internal string? DownloadingModelId { get; private set; }
    internal string? RemovingModelId { get; private set; }
    internal long Generation { get; private set; }
    internal bool SupportsModelRemoval => _lease?.Engine.SupportsModelRemoval == true;
    internal bool CanRemoveModel(string modelId) => Enabled && SupportsModelRemoval &&
        ActiveModelId != modelId && _lease?.Engine.SelectedModelId != modelId;
    // A profile saved by an x64 build may be opened by the ARM64 build, which has no CUDA runtime.
    internal TranscriptionAccelerationPreference Acceleration =>
        Enum.TryParse<TranscriptionAccelerationPreference>(_host.GetSetting<string>(AccelerationSetting), out var saved)
            && AccelerationChoices.Contains(saved) && (saved != TranscriptionAccelerationPreference.NvidiaCuda || CudaArchitecture)
            ? saved : TranscriptionAccelerationPreference.Auto;
    // The CUDA runtime exists only for 64-bit Windows.
    private static bool CudaArchitecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
    internal bool SupportsCuda => CudaArchitecture
        && _lease?.Engine.SupportedAccelerationBackends.Contains(TranscriptionAccelerationBackend.NvidiaCuda) == true;
    // How the loaded model runs; null while no model is loaded.
    internal TranscriptionAccelerationBackend? ActiveBackend => Ready ? _lease!.Engine.AccelerationStatus.ActiveBackend : null;
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

    internal LocalTranscriptionPlugin(IPluginHostServices? host = null, Func<Task<LocalTranscriptionLease>>? load = null, Func<string>? packageDirectory = null,
        TranscriptionIsolation? isolation = null)
    {
        _isolation = isolation;
        _packageDirectory = packageDirectory ?? (() => Path.Combine(AppContext.BaseDirectory, "Plugins", PluginId));
        _host = host ?? new VocabularyHostServices(WinUIProfile.DataPath("PluginData", PluginId),
            assetDirectory: WinUIProfile.PluginAssetPath(PluginId));
        _load = load ?? LoadPackageAsync;
    }

    private async Task<LocalTranscriptionLease> LoadPackageAsync()
    {
        var directory = _packageDirectory();
        var package = await PortablePluginPackage.LoadAsync(directory, _host, LocalCtcVocabulary.HostVersion);
        if (package.Plugin is IPcmTranscriptionEnginePlugin engine)
            return _isolation?.TryIsolate(engine, directory, _host) is { } isolated
                ? new(isolated, new IsolatedLifetime(isolated, package))
                : new(engine, package);
        await package.DisposeAsync();
        throw new NotSupportedException("The local plugin does not provide PCM transcription.");
    }

    internal async Task InitializeAsync()
    {
        if (_host.GetSetting<bool?>("Enabled") != false) await SetEnabledAsync(true);
    }

    internal async Task SetEnabledAsync(bool enabled)
    {
        if (!await _operations.WaitAsync(0)) throw new InvalidOperationException(Loc.T("Finish or cancel the current model operation first."));
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
            Generation++;
            _lease.Engine.SetAccelerationPreference(Acceleration);
            try { _host.SetSetting("Enabled", true); }
            catch { await ReleaseAsync(); throw; }
            var selected = _host.GetSetting<string>("SelectedModelId") ?? _lease.Engine.SelectedModelId
                ?? Models.FirstOrDefault(model => model.Model.IsRecommended)?.Model.Id
                ?? Models.FirstOrDefault()?.Model.Id;
            if (selected is null || !Models.Any(m => m.Model.Id == selected && m.Downloaded))
            {
                Feedback = Loc.T("Choose a downloaded model or download one below.");
                return;
            }
            // An activation failure keeps the package available for download/retry.
            try { await ActivateCoreAsync(selected, CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Error = Loc.T("Could not load model: {0}", ex.Message); }
        }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    internal async Task ActivateAsync(string modelId, CancellationToken ct = default, bool persistSelection = true)
    {
        if (!await _operations.WaitAsync(0, ct)) throw new InvalidOperationException(Loc.T("A model operation is already in progress."));
        Busy = true; Error = null; Feedback = null; Changed?.Invoke();
        try { ObjectDisposedException.ThrowIf(_disposed, this); await ActivateCoreAsync(modelId, ct, persistSelection); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = Loc.T("Could not load model: {0}", ex.Message); throw; }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    // A loaded model is loaded again, which starts a worker with the new device. CUDA downloads its runtime
    // on the first load. If that load fails, the previous device and model are restored.
    internal async Task SetAccelerationAsync(TranscriptionAccelerationPreference preference, CancellationToken ct = default)
    {
        if (!AccelerationChoices.Contains(preference) || preference == TranscriptionAccelerationPreference.NvidiaCuda && !CudaArchitecture)
            throw new ArgumentOutOfRangeException(nameof(preference));
        if (Acceleration == preference) return;
        if (!await _operations.WaitAsync(0, ct)) throw new InvalidOperationException(Loc.T("A model operation is already in progress."));
        Busy = true; Error = null;
        Feedback = preference == TranscriptionAccelerationPreference.NvidiaCuda
            ? Loc.T("Switching to NVIDIA CUDA… The first switch downloads about 2 GB.") : null;
        Changed?.Invoke();
        string? model = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var engine = _lease?.Engine ?? throw new InvalidOperationException(Loc.T("Enable the local plugin in Plugins first."));
            var previous = Acceleration;
            model = ActiveModelId;
            _host.SetSetting(AccelerationSetting, preference.ToString());
            engine.SetAccelerationPreference(preference);
            if (model is null) { Feedback = null; return; }
            ActiveModelId = null;
            try
            {
                await engine.LoadModelAsync(model, ct);
                ActiveModelId = model;
                Feedback = Loc.T("Model ready for dictation.");
            }
            catch
            {
                _host.SetSetting(AccelerationSetting, previous.ToString());
                engine.SetAccelerationPreference(previous);
                try { await engine.LoadModelAsync(model, CancellationToken.None); ActiveModelId = model; }
                catch (Exception rollback) when (rollback is not OutOfMemoryException)
                { _host.Log(PluginLogLevel.Error, "Could not restore the previous processing device: " + rollback.Message); }
                throw;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Feedback = null;
            // When the previous model could not be loaded again either, no model is ready for dictation.
            Error = model is not null && ActiveModelId is null
                ? Loc.T("Could not change the processing device: {0} The model is not loaded; choose Use model to load it again.", ex.Message)
                : Loc.T("Could not change the processing device: {0}", ex.Message);
            throw;
        }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    private async Task ActivateCoreAsync(string modelId, CancellationToken ct, bool persistSelection = true)
    {
        var engine = _lease?.Engine ?? throw new InvalidOperationException(Loc.T("Enable the local plugin in Plugins first."));
        if (!Models.Any(m => m.Model.Id == modelId && m.Downloaded)) throw new InvalidOperationException(Loc.T("Download this model before selecting it."));
        if (ActiveModelId == modelId) return;
        var previous = ActiveModelId;
        ActiveModelId = null;
        try
        {
            await engine.LoadModelAsync(modelId, ct);
            ct.ThrowIfCancellationRequested();
            if (persistSelection) _host.SetSetting("SelectedModelId", modelId);
            ActiveModelId = modelId;
            Feedback = Loc.T("Model ready for dictation.");
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

    internal async Task<string?> UnloadAsync(CancellationToken ct)
    {
        if (!await _operations.WaitAsync(0, ct)) throw new InvalidOperationException(Loc.T("A model operation is already in progress."));
        Busy = true; Error = null; Changed?.Invoke();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var engine = _lease?.Engine ?? throw new InvalidOperationException(Loc.T("Enable the local plugin first."));
            ct.ThrowIfCancellationRequested();
            var previous = ActiveModelId;
            await engine.UnloadModelAsync();
            ActiveModelId = null;
            Feedback = Loc.T("Model unloaded. Select a downloaded model to use it again.");
            ct.ThrowIfCancellationRequested();
            return previous;
        }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    internal async Task RestoreRequestModelAsync(string? activeModel, string? selectedModel)
    {
        await _operations.WaitAsync();
        Busy = true;
        try
        {
            if (activeModel is not null) await ActivateCoreAsync(activeModel, CancellationToken.None, persistSelection: false);
            else
            {
                await _lease!.Engine.UnloadModelAsync();
                ActiveModelId = null;
                if (selectedModel is not null) await _lease.Engine.SelectModelAsync(selectedModel, CancellationToken.None);
                else if (_lease.Engine.SelectedModelId is not null)
                {
                    // SDK SelectModel cannot clear selection; a fresh package
                    // restores the original unselected state without host writes.
                    await ReleaseAsync();
                    _lease = await _load();
                    Generation++;
                    _lease.Engine.SetAccelerationPreference(Acceleration);
                    if (_lease.Engine.SelectedModelId is not null)
                        throw new InvalidOperationException("The local plugin did not restore its unselected state.");
                }
            }
        }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }

    internal async Task DownloadAsync(string modelId, CancellationToken ct = default, bool propagateErrors = false)
    {
        if (!await _operations.WaitAsync(0, ct)) throw new InvalidOperationException(Loc.T("A model operation is already in progress."));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _download = cancellation;
        Busy = true; Progress = 0; Error = null; Feedback = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var engine = _lease?.Engine ?? throw new InvalidOperationException(Loc.T("Enable the local plugin in Plugins first."));
            if (!engine.TranscriptionModels.Any(m => m.Id == modelId)) throw new ArgumentException(Loc.T("Unknown model."));
            if (!engine.SupportsModelDownload) throw new NotSupportedException(Loc.T("This plugin does not support downloads."));
            DownloadingModelId = modelId; Changed?.Invoke();
            await engine.DownloadModelAsync(modelId, new InlineProgress(value =>
            {
                if (!ReferenceEquals(_download, cancellation) || !double.IsFinite(value)) return;
                Progress = Math.Max(Progress, Math.Clamp(value, 0, 1)); Changed?.Invoke();
            }), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!engine.IsModelDownloaded(modelId)) throw new IOException(Loc.T("The download did not produce a complete model."));
            Feedback = Loc.T("Download complete. Choose Use model to activate it.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Feedback = Loc.T("Download canceled. Your active model is unchanged."); if (propagateErrors) throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = ModelStorageSpace.DescribeFailure(ex) ?? Loc.T("Download failed: {0}", ex.Message); if (propagateErrors) throw; }
        finally { _download = null; DownloadingModelId = null; Busy = false; _operations.Release(); Changed?.Invoke(); }
    }
    internal void CancelDownload() => _download?.Cancel();

    internal async Task RemoveAsync(string modelId, long expectedGeneration, CancellationToken ct)
    {
        if (!await _operations.WaitAsync(0, ct)) throw new InvalidOperationException(Loc.T("A model operation is already in progress."));
        Busy = true; Error = null; Feedback = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Generation != expectedGeneration) throw new InvalidOperationException(Loc.T("The plugin changed. Reopen its settings before removing a model."));
            var engine = _lease?.Engine ?? throw new InvalidOperationException(Loc.T("Enable the plugin before managing its models."));
            if (!engine.TranscriptionModels.Any(m => m.Id == modelId)) throw new ArgumentException(Loc.T("Unknown model."), nameof(modelId));
            if (!engine.SupportsModelRemoval) throw new NotSupportedException(Loc.T("This plugin does not support model removal."));
            if (!CanRemoveModel(modelId)) throw new InvalidOperationException(Loc.T("Select a different model in this plugin before removing this one."));
            ct.ThrowIfCancellationRequested();
            RemovingModelId = modelId; Changed?.Invoke();
            if (engine.IsModelDownloaded(modelId))
                await engine.RemoveModelAsync(modelId, ct);
            ct.ThrowIfCancellationRequested();
            if (engine.IsModelDownloaded(modelId)) throw new IOException(Loc.T("The plugin still reports this model as downloaded."));
            Feedback = Loc.T("Model removed. Download it again to use it.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Feedback = Loc.T("Removal canceled. Refresh model status; some files may already have been removed."); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = Loc.T("Could not remove model: {0}", ex.Message); throw; }
        finally { RemovingModelId = null; Busy = false; _operations.Release(); Changed?.Invoke(); }
    }
    // Starts a worker that was released after inactivity, so the model loads while the user speaks.
    internal void PrepareForDictation()
    {
        if (Ready && _lease?.Engine is IsolatedTranscriptionEngine isolated) _ = PrepareAsync(isolated);
    }

    // Failures surface on the transcription itself.
    private async Task PrepareAsync(IsolatedTranscriptionEngine engine)
    {
        try { await engine.PrepareAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _host.Log(PluginLogLevel.Warning, "Could not prepare the model: " + ex.Message); }
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }

    // Ends the worker before the in-process plugin instance is deactivated.
    private sealed class IsolatedLifetime(IsolatedTranscriptionEngine engine, IAsyncDisposable package) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await engine.DisposeAsync(); }
            finally { await package.DisposeAsync(); }
        }
    }

    internal async Task<(string Text, VocabularyTokenTiming[] Timings, string? DetectedLanguage, float? NoSpeechProbability)> DecodeAsync(float[] samples, bool includeTimings, bool translate = false, CancellationToken ct = default, string? language = null)
    {
        var result = await DecodeResultAsync(samples, language ?? Language, translate, ct);
        return (result.Text, includeTimings ? result.TokenTimings.ToArray() : [], result.DetectedLanguage, result.NoSpeechProbability);
    }

    internal async Task<TypeWhisper.PluginSDK.Models.PluginTranscriptionResult> DecodeResultAsync(float[] samples,
        string? language, bool translate, CancellationToken ct)
    {
        if (!Ready) throw new InvalidOperationException(Loc.T("Choose and load a model before dictating."));
        if (translate && !SupportsTranslation)
            throw new NotSupportedException(Loc.T("The selected local model cannot translate audio to English. Choose a translation-capable model or switch to Transcribe."));
        ct.ThrowIfCancellationRequested();
        var result = await _lease!.Engine.TranscribePcmAsync(samples,
            SpokenLanguageChoices.ForEngine(SupportedLanguages, language), translate, ct);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private async Task ReleaseAsync()
    {
        Generation++;
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
