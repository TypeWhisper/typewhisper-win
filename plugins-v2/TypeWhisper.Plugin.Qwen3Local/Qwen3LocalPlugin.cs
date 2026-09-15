using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Local;

/// <summary>Downloads and runs Qwen3-ASR locally through the portable plugin contract.</summary>
public sealed class Qwen3LocalPlugin : IPcmTranscriptionEnginePlugin
{
    internal const string ModelId = "qwen3-asr-0.6b-int8";
    private static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>
    {
        ["zh"] = "Chinese", ["en"] = "English", ["yue"] = "Cantonese", ["ar"] = "Arabic", ["de"] = "German",
        ["fr"] = "French", ["es"] = "Spanish", ["pt"] = "Portuguese", ["id"] = "Indonesian", ["it"] = "Italian",
        ["ko"] = "Korean", ["ru"] = "Russian", ["th"] = "Thai", ["vi"] = "Vietnamese", ["ja"] = "Japanese",
        ["tr"] = "Turkish", ["hi"] = "Hindi", ["ms"] = "Malay", ["nl"] = "Dutch", ["sv"] = "Swedish",
        ["da"] = "Danish", ["fi"] = "Finnish", ["pl"] = "Polish", ["cs"] = "Czech", ["fil"] = "Filipino",
        ["fa"] = "Persian", ["el"] = "Greek", ["hu"] = "Hungarian", ["mk"] = "Macedonian", ["ro"] = "Romanian"
    };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HttpClient _http;
    private readonly QwenModelAssets _assets;
    private readonly Func<string, IQwenRecognizer> _factory;
    private IPluginHostServices? _host;
    private IQwenRecognizer? _recognizer;
    private string? _selected;
    private bool _disposed;

    /// <summary>Creates the local Qwen provider.</summary>
    public Qwen3LocalPlugin() : this(new HttpClient { Timeout = TimeSpan.FromHours(2) }, path => new QwenRecognizer(path)) { }
    internal Qwen3LocalPlugin(HttpClient http, Func<string, IQwenRecognizer> factory, QwenAssetSource? source = null)
    { _http = http; _factory = factory; _assets = new(http, source); }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.qwen3-local";
    /// <inheritdoc />
    public string PluginName => "Qwen3 ASR (Local)";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => "qwen3-local";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public bool IsConfigured => true;
    /// <inheritdoc />
    public string? SelectedModelId => _selected;
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public bool SupportsModelDownload => true;
    /// <inheritdoc />
    public bool SupportsModelRemoval => true;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => Languages.Keys.ToArray();
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new(ModelId, "Qwen3-ASR 0.6B INT8")
    {
        Publisher = "Qwen", SizeDescription = "~879 MB download · ~1 GB installed", EstimatedSizeMB = 879,
        LanguageCount = Languages.Count, LanguageCodes = Languages.Keys.ToArray()
    }];

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    { ObjectDisposedException.ThrowIf(_disposed, this); _host = host; return Task.CompletedTask; }
    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Release(); _host = null; }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public void SelectModel(string modelId) { ValidateModel(modelId); _selected = modelId; }
    /// <inheritdoc />
    public bool IsModelDownloaded(string modelId) => modelId == ModelId && _host is not null && _assets.IsReady(ModelDirectory);
    /// <inheritdoc />
    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureActive(); await _assets.DownloadAsync(ModelDirectory, progress, ct).ConfigureAwait(false); _host!.NotifyCapabilitiesChanged(); }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public async Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureActive(); ct.ThrowIfCancellationRequested(); Release();
            if (Directory.Exists(ModelDirectory)) Directory.Delete(ModelDirectory, true);
            _selected = null;
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
    { ct.ThrowIfCancellationRequested(); return TranscribePcmAsync(QwenAudio.DecodeWav(wavAudio), language, translate, ct); }
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples, string? language, bool translate, CancellationToken cancellationToken)
    {
        if (translate) throw new NotSupportedException("Qwen3 ASR transcribes speech; audio translation is not supported.");
        var hint = NormalizeLanguage(language);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive(); cancellationToken.ThrowIfCancellationRequested();
            if (samples.Span.ContainsAnyExceptInRange(-1f, 1f))
                throw new ArgumentException("PCM samples must be finite and normalized to [-1, 1].", nameof(samples));
            if (samples.IsEmpty) return new("", null, 0, null);
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                var segments = new List<PluginTranscriptionSegment>();
                for (var offset = 0; offset < samples.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = QwenAudio.ChunkLength(samples.Span[offset..]);
                    var chunk = samples.Slice(offset, length).ToArray();
                    // Native decoding cannot be interrupted safely. Drain the current bounded
                    // window before honoring cancellation; never publish partial success.
                    var text = chunk.All(value => value == 0) ? "" : _recognizer!.Decode(chunk, hint);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (text.Length > 0) segments.Add(new(text, (double)offset / QwenAudio.SampleRate, (double)(offset + length) / QwenAudio.SampleRate));
                    offset += length;
                }
                // sherpa-onnx's .NET result strips the language prefix and exposes no detected language.
                return new PluginTranscriptionResult(string.Join(" ", segments.Select(s => s.Text)), null,
                    (double)samples.Length / QwenAudio.SampleRate, null) { Segments = segments };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private string ModelDirectory => Path.Combine(_host!.PluginAssetDirectory, "Models", ModelId);
    private void EnsureActive()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (_host is null) throw new InvalidOperationException("Qwen plugin is not active."); }
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        EnsureActive(); ct.ThrowIfCancellationRequested();
        if (_recognizer is not null) return;
        if (!_assets.IsReady(ModelDirectory)) throw new InvalidOperationException("Download the Qwen3-ASR model before using it.");
        var loaded = await Task.Run(() => _factory(ModelDirectory), ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) { loaded.Dispose(); ct.ThrowIfCancellationRequested(); }
        _recognizer = loaded;
    }
    private void Release() { _recognizer?.Dispose(); _recognizer = null; }
    private static void ValidateModel(string modelId)
    { if (modelId != ModelId) throw new ArgumentException("Unknown Qwen model.", nameof(modelId)); }
    internal static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var code = language.Trim().ToLowerInvariant().Split('-', '_')[0];
        return Languages.TryGetValue(code, out var name) ? name : throw new ArgumentException("Unsupported Qwen language.", nameof(language));
    }
    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; Release(); _host = null; _http.Dispose(); }
        finally { _gate.Release(); }
    }
}
