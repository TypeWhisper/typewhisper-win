using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using SherpaOnnx;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx;

public sealed class SherpaOnnxPlugin : ITypeWhisperPlugin, ITranscriptionEnginePlugin
{
    private const string ParakeetRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main";
    private const string CanaryRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/main";

    private static readonly IReadOnlyList<string> CanarySupportedLanguages = ["en", "de", "fr", "es"];

    private static readonly IReadOnlyList<ModelDefinition> Models =
    [
        new("parakeet-tdt-0.6b", "Parakeet TDT 0.6B", "~670 MB", 670, 25, true, false,
        [
            new("encoder.int8.onnx", $"{ParakeetRepo}/encoder.int8.onnx", 652),
            new("decoder.int8.onnx", $"{ParakeetRepo}/decoder.int8.onnx", 12),
            new("joiner.int8.onnx", $"{ParakeetRepo}/joiner.int8.onnx", 6),
            new("tokens.txt", $"{ParakeetRepo}/tokens.txt", 1)
        ]),
        new("canary-180m-flash", "Canary 180M Flash", "~198 MB", 198, 4, false, true,
        [
            new("encoder.int8.onnx", $"{CanaryRepo}/encoder.int8.onnx", 127),
            new("decoder.int8.onnx", $"{CanaryRepo}/decoder.int8.onnx", 71),
            new("tokens.txt", $"{CanaryRepo}/tokens.txt", 1)
        ])
    ];

    private readonly object _sync = new();
    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private OfflineRecognizer? _recognizer;
    private string? _loadedModelId;
    private string? _loadedModelDir;
    private string? _selectedModelId;

    // Canary-specific state
    private string _canarySrcLang = "en";
    private string _canaryTgtLang = "en";

    // ITypeWhisperPlugin
    public string PluginId => "com.typewhisper.sherpa-onnx";
    public string PluginName => "Lokale Modelle (sherpa-onnx)";
    public string PluginVersion => "1.0.0";

    // ITranscriptionEnginePlugin
    public string ProviderId => "sherpa-onnx";
    public string ProviderDisplayName => "Lokal (sherpa-onnx)";
    public bool IsConfigured => true;
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => _selectedModelId == "canary-180m-flash";
    public bool SupportsModelDownload => true;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = Models.Select(m =>
        new PluginModelInfo(m.Id, m.DisplayName)
        {
            SizeDescription = m.SizeDescription,
            EstimatedSizeMB = m.EstimatedSizeMB,
            IsRecommended = m.IsRecommended,
            LanguageCount = m.LanguageCount,
        }).ToList();

    public IReadOnlyList<string> SupportedLanguages =>
        _selectedModelId == "canary-180m-flash" ? CanarySupportedLanguages : [];

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        MigrateModelFiles();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        UnloadRecognizer();
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView() => null;

    public void SelectModel(string modelId)
    {
        _ = GetModelDefinition(modelId);
        _selectedModelId = modelId;
    }

    public bool IsModelDownloaded(string modelId)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        return model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName)));
    }

    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);
        Directory.CreateDirectory(dir);

        var totalBytes = model.Files.Sum(f => (long)f.EstimatedSizeMB * 1024 * 1024);
        long cumulativeBytesRead = 0;

        foreach (var file in model.Files)
        {
            var filePath = Path.Combine(dir, file.FileName);
            if (File.Exists(filePath)) continue;

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var buffer = new byte[81920];
            long fileBytesRead = 0;
            var lastReport = DateTime.UtcNow;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using (var fileStream = new FileStream(filePath + ".tmp", FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, true))
            {
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    fileBytesRead += read;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds > 250 && totalBytes > 0)
                    {
                        progress?.Report((double)(cumulativeBytesRead + fileBytesRead) / totalBytes);
                        lastReport = now;
                    }
                }
            }

            File.Move(filePath + ".tmp", filePath, overwrite: true);
            cumulativeBytesRead += fileBytesRead;
        }

        progress?.Report(1.0);
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        var model = GetModelDefinition(modelId);
        var dir = GetModelDirectory(modelId);

        if (!model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName))))
            throw new FileNotFoundException($"Model files not found for: {modelId}");

        return Task.Run(() =>
        {
            lock (_sync)
            {
                UnloadRecognizerUnsafe();

                _recognizer = model.SupportsTranslation
                    ? CreateCanaryRecognizer(dir, "en", "en")
                    : CreateParakeetRecognizer(dir);

                _loadedModelId = modelId;
                _loadedModelDir = dir;
                _selectedModelId = modelId;
                _canarySrcLang = "en";
                _canaryTgtLang = "en";

                Debug.WriteLine($"[SherpaOnnx] Model {modelId} loaded from {dir}");
            }
        }, ct);
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var audioSamples = DecodeWav(wavAudio);
            var audioDuration = audioSamples.Length / 16000.0;

            lock (_sync)
            {
                if (_recognizer is null || _loadedModelId is null)
                    throw new InvalidOperationException("Kein Modell geladen. LoadModelAsync zuerst aufrufen.");

                var model = GetModelDefinition(_loadedModelId);

                if (model.SupportsTranslation)
                    EnsureCanaryLanguage(language, translate);

                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(16000, audioSamples);
                _recognizer.Decode(stream);

                var rawText = stream.Result.Text.Trim();

                var (text, detectedLanguage) = model.SupportsTranslation
                    ? ParseCanaryResult(rawText)
                    : (rawText, (string?)null);

                return new PluginTranscriptionResult(text, detectedLanguage, audioDuration, NoSpeechProbability: null);
            }
        }, ct);
    }

    public void Dispose()
    {
        UnloadRecognizer();
        _httpClient.Dispose();
    }

    // --- Private helpers ---

    private string GetModelDirectory(string modelId) =>
        Path.Combine(_host?.PluginDataDirectory ?? ".", "Models", modelId);

    private static ModelDefinition GetModelDefinition(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId)
        ?? throw new ArgumentException($"Unknown model: {modelId}");

    private void UnloadRecognizer()
    {
        lock (_sync)
            UnloadRecognizerUnsafe();
    }

    private void UnloadRecognizerUnsafe()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _loadedModelId = null;
        _loadedModelDir = null;
        _canarySrcLang = "en";
        _canaryTgtLang = "en";
    }

    private static OfflineRecognizer CreateParakeetRecognizer(string modelDir)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    private static OfflineRecognizer CreateCanaryRecognizer(string modelDir, string srcLang, string tgtLang)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Canary.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Canary.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Canary.SrcLang = srcLang;
        config.ModelConfig.Canary.TgtLang = tgtLang;
        config.ModelConfig.Canary.UsePnc = 1;
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    private void EnsureCanaryLanguage(string? language, bool translate)
    {
        if (_loadedModelDir is null) return;

        var srcLang = NormalizeCanaryLanguage(language);
        var tgtLang = translate ? "en" : srcLang;

        if (srcLang == _canarySrcLang && tgtLang == _canaryTgtLang) return;

        _recognizer?.Dispose();
        _recognizer = CreateCanaryRecognizer(_loadedModelDir, srcLang, tgtLang);
        _canarySrcLang = srcLang;
        _canaryTgtLang = tgtLang;
    }

    private static string NormalizeCanaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto")
            return "en";
        var normalized = language.Trim().ToLowerInvariant();
        return CanarySupportedLanguages.Contains(normalized) ? normalized : "en";
    }

    private static (string Text, string? DetectedLanguage) ParseCanaryResult(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return (string.Empty, null);

        try
        {
            using var json = JsonDocument.Parse(rawText);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return (rawText.Trim(), null);

            var text = rawText.Trim();
            if (json.RootElement.TryGetProperty("text", out var textNode))
                text = textNode.GetString()?.Trim() ?? string.Empty;

            string? lang = null;
            if (json.RootElement.TryGetProperty("lang", out var langNode))
            {
                var parsed = langNode.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                    lang = parsed;
            }

            return (text, lang);
        }
        catch (JsonException)
        {
            return (rawText.Trim(), null);
        }
    }

    private static float[] DecodeWav(byte[] wavData)
    {
        // WAV header: 44 bytes minimum, samples start after data chunk header
        if (wavData.Length < 44)
            throw new ArgumentException("Invalid WAV data: too short");

        // Find data chunk
        var pos = 12; // Skip RIFF header
        while (pos + 8 < wavData.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wavData, pos, 4);
            var chunkSize = BitConverter.ToInt32(wavData, pos + 4);

            if (chunkId == "data")
            {
                var dataStart = pos + 8;
                var sampleCount = chunkSize / 2; // 16-bit samples
                var samples = new float[sampleCount];
                for (var i = 0; i < sampleCount && dataStart + i * 2 + 1 < wavData.Length; i++)
                {
                    var sample = BitConverter.ToInt16(wavData, dataStart + i * 2);
                    samples[i] = sample / 32768f;
                }
                return samples;
            }

            pos += 8 + chunkSize;
            if (chunkSize % 2 != 0) pos++; // Padding byte
        }

        throw new ArgumentException("Invalid WAV data: no data chunk found");
    }

    /// <summary>
    /// Migrates model files from the old location (%LocalAppData%/TypeWhisper/Models/)
    /// to the plugin's data directory on first activation.
    /// </summary>
    private void MigrateModelFiles()
    {
        if (_host is null) return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldModelsDir = Path.Combine(localAppData, "TypeWhisper", "Models");

        if (!Directory.Exists(oldModelsDir)) return;

        foreach (var model in Models)
        {
            var oldDir = Path.Combine(oldModelsDir, model.Id);
            if (!Directory.Exists(oldDir)) continue;

            var newDir = GetModelDirectory(model.Id);
            if (Directory.Exists(newDir) && model.Files.All(f => File.Exists(Path.Combine(newDir, f.FileName))))
                continue; // Already migrated

            Directory.CreateDirectory(newDir);

            foreach (var file in model.Files)
            {
                var oldPath = Path.Combine(oldDir, file.FileName);
                var newPath = Path.Combine(newDir, file.FileName);

                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    try
                    {
                        File.Move(oldPath, newPath);
                        Debug.WriteLine($"[SherpaOnnx] Migrated {file.FileName} for {model.Id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SherpaOnnx] Failed to migrate {file.FileName}: {ex.Message}");
                    }
                }
            }

            // Clean up old directory if empty
            try
            {
                if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                    Directory.Delete(oldDir);
            }
            catch { /* ignore */ }
        }
    }

    private sealed record ModelDefinition(
        string Id,
        string DisplayName,
        string SizeDescription,
        int EstimatedSizeMB,
        int LanguageCount,
        bool IsRecommended,
        bool SupportsTranslation,
        IReadOnlyList<ModelFileDefinition> Files);

    private sealed record ModelFileDefinition(string FileName, string DownloadUrl, int EstimatedSizeMB);
}
