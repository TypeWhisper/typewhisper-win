using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SherpaOnnx;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

public sealed class ParakeetTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    private enum LoadedModelKind
    {
        Unknown,
        ParakeetTransducer,
        Canary
    }

    private static readonly HashSet<string> CanarySupportedLanguages =
    [
        "en", "de", "fr", "es"
    ];

    private readonly object _sync = new();
    private OfflineRecognizer? _recognizer;
    private LoadedModelKind _loadedModelKind = LoadedModelKind.Unknown;
    private string? _modelDir;
    private string _canarySrcLang = "en";
    private string _canaryTgtLang = "en";

    public bool IsModelLoaded
    {
        get
        {
            lock (_sync)
            {
                return _recognizer is not null;
            }
        }
    }

    public Task LoadModelAsync(string modelDir, CancellationToken cancellationToken = default)
    {
        UnloadModel();

        return Task.Run(() =>
        {
            lock (_sync)
            {
                var joinerPath = Path.Combine(modelDir, "joiner.int8.onnx");
                if (File.Exists(joinerPath))
                {
                    _recognizer = CreateParakeetRecognizer(modelDir);
                    _loadedModelKind = LoadedModelKind.ParakeetTransducer;
                    _modelDir = modelDir;
                    Debug.WriteLine($"Parakeet model loaded from {modelDir}");
                    return;
                }

                _recognizer = CreateCanaryRecognizer(modelDir, "en", "en");
                _loadedModelKind = LoadedModelKind.Canary;
                _modelDir = modelDir;
                _canarySrcLang = "en";
                _canaryTgtLang = "en";
                Debug.WriteLine($"Canary model loaded from {modelDir}");
            }
        }, cancellationToken);
    }

    public void UnloadModel()
    {
        lock (_sync)
        {
            _recognizer?.Dispose();
            _recognizer = null;
            _loadedModelKind = LoadedModelKind.Unknown;
            _modelDir = null;
            _canarySrcLang = "en";
            _canaryTgtLang = "en";
        }
    }

    public Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var audioDuration = audioSamples.Length / 16000.0;
            string text;
            string? detectedLanguage = null;

            lock (_sync)
            {
                if (_recognizer is null)
                    throw new InvalidOperationException("No model loaded. Call LoadModelAsync first.");

                if (_loadedModelKind == LoadedModelKind.Canary)
                    EnsureCanaryRecognizer(language, task);

                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(16000, audioSamples);

                _recognizer.Decode(stream);

                var rawText = stream.Result.Text.Trim();
                if (_loadedModelKind == LoadedModelKind.Canary)
                {
                    var parsed = ParseCanaryOutput(rawText);
                    text = parsed.Text;
                    detectedLanguage = parsed.DetectedLanguage;
                }
                else
                {
                    text = rawText;
                }
            }

            sw.Stop();

            var segments = new List<TranscriptionSegment>();
            if (!string.IsNullOrEmpty(text))
                segments.Add(new TranscriptionSegment(text, 0, audioDuration));

            return new TranscriptionResult
            {
                Text = text,
                DetectedLanguage = detectedLanguage,
                Duration = audioDuration,
                ProcessingTime = sw.Elapsed.TotalSeconds,
                Segments = segments
            };
        }, cancellationToken);
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
        var encoderPath = Path.Combine(modelDir, "encoder.int8.onnx");
        var decoderPath = Path.Combine(modelDir, "decoder.int8.onnx");
        var tokensPath = Path.Combine(modelDir, "tokens.txt");

        if (!File.Exists(encoderPath) || !File.Exists(decoderPath) || !File.Exists(tokensPath))
            throw new FileNotFoundException($"Canary model files not found in: {modelDir}");

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Canary.Encoder = encoderPath;
        config.ModelConfig.Canary.Decoder = decoderPath;
        config.ModelConfig.Canary.SrcLang = srcLang;
        config.ModelConfig.Canary.TgtLang = tgtLang;
        config.ModelConfig.Canary.UsePnc = 1;
        config.ModelConfig.Tokens = tokensPath;
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        return new OfflineRecognizer(config);
    }

    private void EnsureCanaryRecognizer(string? language, TranscriptionTask task)
    {
        if (_loadedModelKind != LoadedModelKind.Canary || _modelDir is null)
            return;

        var srcLang = NormalizeCanaryLanguage(language);
        var tgtLang = task == TranscriptionTask.Translate ? "en" : srcLang;

        if (srcLang == _canarySrcLang && tgtLang == _canaryTgtLang)
            return;

        _recognizer?.Dispose();
        _recognizer = CreateCanaryRecognizer(_modelDir, srcLang, tgtLang);
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

    private static (string Text, string? DetectedLanguage) ParseCanaryOutput(string rawText)
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

            string? language = null;
            if (json.RootElement.TryGetProperty("lang", out var langNode))
            {
                var parsedLanguage = langNode.GetString();
                if (!string.IsNullOrWhiteSpace(parsedLanguage))
                    language = parsedLanguage;
            }

            return (text, language);
        }
        catch (JsonException)
        {
            return (rawText.Trim(), null);
        }
    }

    public void Dispose()
    {
        UnloadModel();
    }
}
