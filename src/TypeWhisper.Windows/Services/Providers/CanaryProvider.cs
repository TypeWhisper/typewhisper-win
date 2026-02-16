using System.IO;
using System.Text.Json;
using SherpaOnnx;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services.Providers;

public sealed class CanaryProvider : LocalProviderBase
{
    private static readonly HashSet<string> SupportedLanguages = ["en", "de", "fr", "es"];

    private const string Repo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/main";

    private string _srcLang = "en";
    private string _tgtLang = "en";

    public override string Id => "canary-180m-flash";
    public override string DisplayName => "Canary 180M Flash";

    public override ModelInfo Model { get; } = new()
    {
        Id = "canary-180m-flash",
        DisplayName = "Canary 180M Flash",
        SizeDescription = "~198 MB",
        EstimatedSizeMB = 198,
        SubDirectory = "canary-180m-flash",
        LanguageCount = 4,
        SupportsTranslation = true,
        Files =
        [
            new("encoder.int8.onnx", $"{Repo}/encoder.int8.onnx", 127),
            new("decoder.int8.onnx", $"{Repo}/decoder.int8.onnx", 71),
            new("tokens.txt", $"{Repo}/tokens.txt", 1)
        ]
    };

    protected override OfflineRecognizer CreateRecognizer(string modelDir)
    {
        return CreateCanaryRecognizer(modelDir, "en", "en");
    }

    protected override void OnBeforeTranscribe(string? language, TranscriptionTask task)
    {
        if (ModelDir is null) return;

        var srcLang = NormalizeLanguage(language);
        var tgtLang = task == TranscriptionTask.Translate ? "en" : srcLang;

        if (srcLang == _srcLang && tgtLang == _tgtLang) return;

        // Recreate recognizer with new language settings
        Recognizer?.Dispose();
        Recognizer = CreateCanaryRecognizer(ModelDir, srcLang, tgtLang);
        _srcLang = srcLang;
        _tgtLang = tgtLang;
    }

    protected override void OnUnloaded()
    {
        _srcLang = "en";
        _tgtLang = "en";
    }

    protected override (string Text, string? DetectedLanguage) ParseResult(string rawText)
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

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto")
            return "en";
        var normalized = language.Trim().ToLowerInvariant();
        return SupportedLanguages.Contains(normalized) ? normalized : "en";
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
}
