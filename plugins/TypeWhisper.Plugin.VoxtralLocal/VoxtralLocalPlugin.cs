using System.Buffers.Binary;
using System.Runtime.InteropServices;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.VoxtralLocal;

/// <summary>
/// Local speech recognition with Mistral's Voxtral Mini 4B Realtime through transcribe.cpp. The model detects the
/// language itself, writes punctuation, capitalization and numbers, and is meant for a graphics card with Vulkan;
/// it also runs on the processor, but only about as fast as speech. Transcription runs after the recording ends.
/// </summary>
public sealed partial class VoxtralLocalPlugin : IPcmTranscriptionEnginePlugin, IPluginTextSettings
{
    internal const int SampleRate = 16000;
    private const string SelectedModelSetting = "selectedModel";
    // https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602: the 13 evaluated languages.
    internal static readonly IReadOnlyList<string> Languages =
        ["ar", "de", "en", "es", "fr", "hi", "it", "ja", "ko", "nl", "pt", "ru", "zh"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly VoxtralLocalModel _model;
    private readonly TranscribeCppAssetStore _runtime;
    private readonly Func<string, string, TranscriptionAccelerationPreference, IVoxtralRecognizer>? _factory;
    private IPluginHostServices? _host;
    private IVoxtralRecognizer? _recognizer;
    private string? _selected;
    private TranscriptionAccelerationPreference _preference = TranscriptionAccelerationPreference.Auto;
    private TranscriptionAccelerationStatus _status = PendingStatus(TranscriptionAccelerationPreference.Auto);
    private bool _disposed;

    /// <summary>Creates the Voxtral provider.</summary>
    public VoxtralLocalPlugin() : this(new HttpClient { Timeout = TimeSpan.FromHours(2) }) { }

    // Tests download through a fake handler with a small model and runtime whose hashes they control, and replace
    // the native recognizer: the factory receives the model path, the runtime directory and the processing device.
    internal VoxtralLocalPlugin(HttpClient http, VoxtralLocalModel? model = null, TranscribeCppAsset? runtime = null,
        Func<string, string, TranscriptionAccelerationPreference, IVoxtralRecognizer>? factory = null)
    {
        _http = http;
        _model = model ?? VoxtralLocalModel.MiniQ4;
        _runtime = new(http, runtime ?? TranscribeCppAsset.Runtime, path => AvailableBytes(path));
        _factory = factory;
        var megabytes = (int)Math.Round((_model.SizeBytes + _runtime.Asset.Size) / (1024d * 1024));
        TranscriptionModels =
        [
            new(_model.Id, _model.DisplayName)
            {
                Publisher = "Mistral AI", SizeDescription = "~2.8 GB", EstimatedSizeMB = megabytes,
                IsRecommended = true, LanguageCount = Languages.Count, LanguageCodes = Languages
            }
        ];
    }

    internal Func<string, long?> AvailableBytes { get; set; } = ModelStorageSpace.GetAvailableBytes;

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.voxtral-local";
    /// <inheritdoc />
    public string PluginName => "Voxtral (Local)";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => "voxtral-local";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public bool IsConfigured => _selected is { } id && IsModelDownloaded(id);
    /// <inheritdoc />
    public string? SelectedModelId => _selected;
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public bool SupportsModelDownload => true;
    /// <inheritdoc />
    public bool SupportsModelRemoval => true;
    /// <summary>
    /// Off for now: live preview would run the 4B model again and again while recording. Voxtral's own streaming
    /// decoder is the better route and is not wired up yet.
    /// </summary>
    public bool SupportsLocalLivePreview => false;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => Languages;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }

    /// <summary>
    /// The processor, and any graphics card with Vulkan. The host labels <see cref="TranscriptionAccelerationBackend.AmdVulkan"/>
    /// as its Vulkan option; it is not limited to AMD.
    /// </summary>
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; } =
        [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.AmdVulkan];
    /// <inheritdoc />
    public TranscriptionAccelerationPreference AccelerationPreference => _preference;
    /// <inheritdoc />
    public TranscriptionAccelerationStatus AccelerationStatus => _status;

    // transcribe.cpp publishes its native runtime for Windows x64 only, which is also all the app ships for.
    internal static bool RuntimePlatform => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host = host;
        _selected = host.GetSetting<string>(SelectedModelSetting) == _model.Id ? _model.Id : null;
        if (Enum.TryParse<TranscriptionAccelerationPreference>(host.GetSetting<string>(AccelerationSetting), out var preference))
            SetAccelerationPreference(preference);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Release(); _host = null; }
        finally { _gate.Release(); }
    }

    /// <summary>Takes effect on the next model load; the host starts a new worker for a changed device.</summary>
    public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
    {
        _preference = preference is TranscriptionAccelerationPreference.Cpu or TranscriptionAccelerationPreference.AmdVulkan
            ? preference : TranscriptionAccelerationPreference.Auto;
        if (_recognizer is null) _status = PendingStatus(_preference);
    }

    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        ValidateModel(modelId);
        EnsureActive();
        _host!.SetSetting(SelectedModelSetting, modelId);
        _selected = modelId;
    }

    /// <summary>Whether the model file and the transcribe.cpp runtime are both in place.</summary>
    public bool IsModelDownloaded(string modelId) => modelId == _model.Id && _host is not null
        && new FileInfo(ModelPath) is { Exists: true } file && file.Length == _model.SizeBytes && _runtime.IsReady(RuntimeDirectory);

    /// <summary>Downloads the runtime (about 17 MB) and the model (about 2.8 GB). An interrupted model download resumes.</summary>
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        ValidateModel(modelId);
        EnsurePlatform();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureActive();
            await DownloadAsync(progress, ct).ConfigureAwait(false);
            _host!.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var runtimeBytes = _runtime.IsReady(RuntimeDirectory) ? 0 : _runtime.Asset.Size;
        var modelReady = new FileInfo(ModelPath) is { Exists: true } file && file.Length == _model.SizeBytes;
        double total = runtimeBytes + _model.SizeBytes;
        if (!modelReady)
        {
            Directory.CreateDirectory(ModelDirectory);
            var partial = new FileInfo(ModelPath + ".download");
            var saved = partial.Exists && partial.Length <= _model.SizeBytes ? partial.Length : 0;
            ModelStorageSpace.EnsureAvailable(ModelDirectory, _model.SizeBytes - saved, _model.DisplayName, AvailableBytes);
        }
        await _runtime.DownloadAsync(RuntimeDirectory, new Progress(value => progress?.Report(value * runtimeBytes / total)), ct)
            .ConfigureAwait(false);
        if (!modelReady)
            await ResumableModelDownloader.DownloadAsync(_http, _model, ModelPath,
                new Progress(value => progress?.Report((runtimeBytes + value * _model.SizeBytes) / total)), ct).ConfigureAwait(false);
        progress?.Report(1);
    }

    /// <summary>Removes the model file. The 17 MB runtime stays, since a running worker may still hold its libraries.</summary>
    public async Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureActive();
            ct.ThrowIfCancellationRequested();
            Release();
            if (Directory.Exists(ModelDirectory)) Directory.Delete(ModelDirectory, recursive: true);
            if (_selected == modelId)
            {
                _host!.SetSetting<string?>(SelectedModelSetting, null);
                _selected = null;
            }
            _host!.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureLoadedAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task UnloadModelAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Release(); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return TranscribePcmAsync(DecodeWav(wavAudio), language, translate, ct);
    }

    /// <summary>
    /// Transcribes 16 kHz mono PCM in one pass. The model detects the language itself, so <paramref name="language"/>
    /// is not passed on; transcribe.cpp accepts about 2.9 hours per call.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples, string? language, bool translate,
        CancellationToken cancellationToken)
    {
        if (translate) throw new NotSupportedException("Voxtral (Local) cannot translate.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (samples.Span.ContainsAnyExceptInRange(-1f, 1f))
                throw new ArgumentException("PCM samples must be finite and normalized to [-1, 1].", nameof(samples));
            var duration = samples.Length / (double)SampleRate;
            if (samples.IsEmpty) return new("", null, 0, null);
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var recognizer = _recognizer!;
            var text = await Task.Run(() => recognizer.Transcribe(samples.ToArray(), cancellationToken), cancellationToken).ConfigureAwait(false);
            return new(text.Trim(), null, duration, null);
        }
        finally { _gate.Release(); }
    }

    private string ModelDirectory => Path.Join(_host!.PluginAssetDirectory, "Models", _model.Id);
    private string ModelPath => Path.Join(ModelDirectory, _model.FileName);
    private string RuntimeDirectory =>
        Path.Join(_host!.PluginAssetDirectory, "Runtimes", "transcribe-cpp", TranscribeCppNative.Version + "-cpu-vulkan");

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is null) throw new InvalidOperationException("The Voxtral (Local) plugin is not active.");
    }

    private static void EnsurePlatform()
    {
        if (!RuntimePlatform) throw new PlatformNotSupportedException("Voxtral (Local) needs Windows on an x64 processor.");
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        EnsureActive();
        ct.ThrowIfCancellationRequested();
        if (_recognizer is not null) return;
        if (!IsModelDownloaded(_model.Id)) throw new InvalidOperationException("Download the Voxtral model before using it.");
        if (_factory is null) EnsurePlatform();
        var (modelPath, runtimeDirectory, preference, host) = (ModelPath, RuntimeDirectory, _preference, _host!);
        IVoxtralRecognizer loaded;
        try { loaded = await Task.Run(() => Open(modelPath, runtimeDirectory, preference, host), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status = new(TranscriptionAccelerationBackend.Cpu,
                preference == TranscriptionAccelerationPreference.Cpu ? "Model not loaded" : "GPU unavailable", ex.Message);
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            loaded.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        _recognizer = loaded;
        _status = loaded.Status;
        host.Log(PluginLogLevel.Info, $"Loaded model {_model.Id} ({_status.DisplayText})");
    }

    private IVoxtralRecognizer Open(string modelPath, string runtimeDirectory, TranscriptionAccelerationPreference preference, IPluginHostServices host) =>
        _factory?.Invoke(modelPath, runtimeDirectory, preference) ?? TranscribeCppVoxtral.Open(modelPath, runtimeDirectory, preference,
            (level, message) =>
            {
                // 2 = WARN, 3 = ERROR; INFO and DEBUG would flood the log with per-run decoder statistics.
                if (level is 2 or 3 && message.Length > 0)
                    host.Log(level == 3 ? PluginLogLevel.Error : PluginLogLevel.Warning, "transcribe.cpp: " + message);
            },
            message => host.Log(PluginLogLevel.Warning, message));

    private void Release()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _status = PendingStatus(_preference);
    }

    private static TranscriptionAccelerationStatus PendingStatus(TranscriptionAccelerationPreference preference) =>
        preference == TranscriptionAccelerationPreference.Cpu
            ? TranscribeCppVoxtral.CpuStatus(null)
            : new(TranscriptionAccelerationBackend.AmdVulkan, "Using GPU");

    private void ValidateModel(string modelId)
    {
        if (modelId != _model.Id) throw new ArgumentException($"Unknown model: {modelId}", nameof(modelId));
    }

    /// <summary>Reads 16-bit mono 16 kHz PCM from a RIFF/WAVE file.</summary>
    internal static float[] DecodeWav(byte[] wav)
    {
        var data = wav.AsSpan();
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new ArgumentException("Expected a PCM16 mono 16 kHz WAV file.", nameof(wav));
        var formatFound = false;
        var position = 12;
        while (position <= data.Length - 8)
        {
            var id = data.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            var available = (int)Math.Min(size, (uint)(data.Length - position - 8));
            var chunk = data.Slice(position + 8, available);
            if (id.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != SampleRate
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16)
                    throw new ArgumentException("Expected PCM16 mono audio sampled at 16 kHz.", nameof(wav));
                formatFound = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!formatFound) break;
                // A recorder that was stopped abruptly can leave a data size larger than the file; read what is there.
                var samples = new float[chunk.Length / 2];
                for (var index = 0; index < samples.Length; index++)
                    samples[index] = BinaryPrimitives.ReadInt16LittleEndian(chunk[(2 * index)..]) / 32768f;
                return samples;
            }
            position += 8 + available + (int)(size % 2);
        }
        throw new ArgumentException("Missing or invalid WAV chunks.", nameof(wav));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            Release();
            _host = null;
            _http.Dispose();
        }
        finally { _gate.Release(); }
    }

    // A synchronous reporter: Progress<T> would post to a captured context and could report out of order.
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
