using System.Buffers.Binary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GigaAm;

/// <summary>
/// Local Russian speech recognition with GigaAM v3 by Sber. The end-to-end RNN-T model adds punctuation and
/// capitalization and runs on the CPU through ONNX Runtime; it only recognizes Russian.
/// </summary>
public sealed class GigaAmPlugin : IPcmTranscriptionEnginePlugin
{
    internal const string ModelId = "gigaam-v3-e2e-rnnt";
    internal const string PreprocessorFileName = "gigaam_v3_preprocessor.onnx";
    private const string SelectedModelSetting = "selectedModel";
    private static readonly IReadOnlyList<string> Russian = ["ru"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly GigaAmModelAssets _assets;
    private readonly Func<string, string, IGigaAmRecognizer> _factory;
    private IPluginHostServices? _host;
    private IGigaAmRecognizer? _recognizer;
    private string? _selected;
    private bool _disposed;

    /// <summary>Creates the GigaAM provider.</summary>
    public GigaAmPlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromHours(2) }, (modelDirectory, preprocessor) => new GigaAmRuntime(modelDirectory, preprocessor))
    {
    }

    // Tests download through a fake handler and a small file set whose hashes they control, and replace the runtime:
    // the factory receives the model directory and the preprocessor path.
    internal GigaAmPlugin(HttpClient http, Func<string, string, IGigaAmRecognizer> factory, GigaAmModelAssets? assets = null)
    {
        _http = http;
        _factory = factory;
        _assets = assets ?? new GigaAmModelAssets(http, GigaAmModelAssets.DefaultBaseUrl, GigaAmModelAssets.DefaultFiles);
        var megabytes = (int)Math.Round(_assets.TotalBytes / (1024d * 1024));
        TranscriptionModels =
        [
            new(ModelId, "GigaAM v3 (Russian, with punctuation)")
            {
                Publisher = "Sber", SizeDescription = $"~{megabytes} MB", EstimatedSizeMB = megabytes, IsRecommended = true,
                LanguageCount = 1, LanguageCodes = Russian
            }
        ];
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.gigaam";
    /// <inheritdoc />
    public string PluginName => "GigaAM";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => "gigaam";
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
    /// <inheritdoc />
    public bool SupportsLocalLivePreview => true;
    /// <summary>GigaAM v3 only recognizes Russian.</summary>
    public IReadOnlyList<string> SupportedLanguages => Russian;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }

    /// <summary>The feature extractor ships with the plugin; only the model weights are downloaded.</summary>
    internal static string PreprocessorPath => Path.Join(Path.GetDirectoryName(typeof(GigaAmPlugin).Assembly.Location)!, PreprocessorFileName);

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host = host;
        _selected = host.GetSetting<string>(SelectedModelSetting) == ModelId ? ModelId : null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Release(); _host = null; }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        ValidateModel(modelId);
        EnsureActive();
        _host!.SetSetting(SelectedModelSetting, modelId);
        _selected = modelId;
    }

    /// <inheritdoc />
    public bool IsModelDownloaded(string modelId) => modelId == ModelId && _host is not null && _assets.IsReady(ModelDirectory);

    /// <inheritdoc />
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureActive();
            await _assets.DownloadAsync(ModelDirectory, TranscriptionModels[0].DisplayName, progress, ct).ConfigureAwait(false);
            _host!.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
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
    /// Transcribes 16 kHz mono PCM as Russian, whatever <paramref name="language"/> asks for: the model knows no other
    /// language. Recordings longer than 20 seconds are cut at quiet points and transcribed chunk by chunk.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples, string? language, bool translate,
        CancellationToken cancellationToken)
    {
        if (translate) throw new NotSupportedException("GigaAM cannot translate. It only transcribes Russian.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (samples.Span.ContainsAnyExceptInRange(-1f, 1f))
                throw new ArgumentException("PCM samples must be finite and normalized to [-1, 1].", nameof(samples));
            if (samples.IsEmpty) return new("", "ru", 0, null);
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var recognizer = _recognizer!;
            return await Task.Run(() => Transcribe(recognizer, samples, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal static PluginTranscriptionResult Transcribe(IGigaAmRecognizer recognizer, ReadOnlyMemory<float> samples, CancellationToken ct)
    {
        const double SampleRate = GigaAmRuntime.SampleRate;
        var transcript = "";
        var segments = new List<PluginTranscriptionSegment>();
        foreach (var (offset, count) in GigaAmChunking.ChunkRanges(samples.Span, GigaAmChunking.MaximumChunkSeconds * GigaAmRuntime.SampleRate))
        {
            ct.ThrowIfCancellationRequested();
            var text = GigaAmChunking.Continuing(transcript, recognizer.Transcribe(samples.Slice(offset, count).ToArray(), ct).Trim());
            ct.ThrowIfCancellationRequested();
            if (text.Length == 0) continue;
            transcript = GigaAmChunking.Append(transcript, text);
            segments.Add(new(text, offset / SampleRate, (offset + count) / SampleRate));
        }
        return new(transcript, "ru", samples.Length / SampleRate, null) { Segments = segments };
    }

    private string ModelDirectory => Path.Join(_host!.PluginAssetDirectory, "Models", ModelId);

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is null) throw new InvalidOperationException("The GigaAM plugin is not active.");
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        EnsureActive();
        ct.ThrowIfCancellationRequested();
        if (_recognizer is not null) return;
        if (!_assets.IsReady(ModelDirectory)) throw new InvalidOperationException("Download the GigaAM model before using it.");
        var directory = ModelDirectory;
        var loaded = await Task.Run(() => _factory(directory, PreprocessorPath), ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            loaded.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        _recognizer = loaded;
        _host!.Log(PluginLogLevel.Info, $"Loaded model {ModelId}");
    }

    private void Release()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    private static void ValidateModel(string modelId)
    {
        if (modelId != ModelId) throw new ArgumentException($"Unknown model: {modelId}", nameof(modelId));
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
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != GigaAmRuntime.SampleRate
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
}
