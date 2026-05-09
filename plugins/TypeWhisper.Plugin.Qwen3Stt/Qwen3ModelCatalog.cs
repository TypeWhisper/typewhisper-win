using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Stt;

public static class Qwen3ModelCatalog
{
    public const string DefaultModelId = "qwen3-asr-0.6b-int4";
    public const string LegacyServerModelId = "Qwen/Qwen3-ASR";
    public const string PluginId = "com.typewhisper.qwen3-stt";
    public const string ModelDirectoryEnvironmentVariable = "TYPEWHISPER_QWEN3_ONNX_MODEL_DIR";
    public const string DirectMlEnvironmentVariable = "TYPEWHISPER_QWEN3_ONNX_ENABLE_DIRECTML";

    private const string Repo06B = "andrewleech/qwen3-asr-0.6b-onnx";
    private const string Repo17B = "andrewleech/qwen3-asr-1.7b-onnx";

    public static readonly IReadOnlyList<Qwen3ModelDefinition> Models =
    [
        new(
            DefaultModelId,
            "Qwen3 ASR 0.6B (int4)",
            Repo06B,
            "qwen3-asr-0.6b-int4.tar.gz",
            "~1.2 GB",
            1_200,
            IsRecommended: true,
            IsQuantized: true,
            HiddenSize: 1024,
            RequiredFiles:
            [
                "encoder.int4.onnx",
                "decoder_init.int4.onnx",
                "decoder_step.int4.onnx",
                "decoder_weights.int4.data",
                "embed_tokens.bin",
                "tokenizer.json",
                "tokenizer_config.json",
                "config.json",
                "preprocessor_config.json"
            ]),
        new(
            "qwen3-asr-0.6b-fp32",
            "Qwen3 ASR 0.6B (FP32)",
            Repo06B,
            "qwen3-asr-0.6b.tar.gz",
            "~2.1 GB",
            2_100,
            IsRecommended: false,
            IsQuantized: false,
            HiddenSize: 1024,
            RequiredFiles:
            [
                "encoder.onnx",
                "decoder_init.onnx",
                "decoder_step.onnx",
                "decoder_weights.data",
                "embed_tokens.bin",
                "tokenizer.json",
                "tokenizer_config.json",
                "config.json",
                "preprocessor_config.json"
            ]),
        new(
            "qwen3-asr-1.7b-int4",
            "Qwen3 ASR 1.7B (int4)",
            Repo17B,
            "qwen3-asr-1.7b-int4.tar.gz",
            "~2.5 GB",
            2_540,
            IsRecommended: false,
            IsQuantized: true,
            HiddenSize: 2048,
            RequiredFiles:
            [
                "encoder.int4.onnx",
                "decoder_init.int4.onnx",
                "decoder_step.int4.onnx",
                "decoder_weights.int4.data",
                "embed_tokens.bin",
                "tokenizer.json",
                "tokenizer_config.json",
                "config.json",
                "preprocessor_config.json"
            ]),
        new(
            "qwen3-asr-1.7b-fp32",
            "Qwen3 ASR 1.7B (FP32)",
            Repo17B,
            "qwen3-asr-1.7b.tar.gz",
            "~5.0 GB",
            5_124,
            IsRecommended: false,
            IsQuantized: false,
            HiddenSize: 2048,
            RequiredFiles:
            [
                "encoder.onnx",
                "decoder_init.onnx",
                "decoder_step.onnx",
                "decoder_weights.data",
                "embed_tokens.bin",
                "tokenizer.json",
                "tokenizer_config.json",
                "config.json",
                "preprocessor_config.json"
            ])
    ];

    public static IReadOnlyList<PluginModelInfo> ToPluginModels() =>
        Models.Select(model => new PluginModelInfo(model.Id, model.DisplayName)
        {
            SizeDescription = model.SizeDescription,
            EstimatedSizeMB = model.EstimatedSizeMB,
            IsRecommended = model.IsRecommended,
            LanguageCount = Qwen3LanguageMapper.SupportedLanguageCodes.Count,
        }).ToList();

    public static Qwen3ModelDefinition GetModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);
        return Models.FirstOrDefault(model => model.Id == normalized)
            ?? throw new ArgumentException($"Unknown Qwen3 ASR model: {modelId}", nameof(modelId));
    }

    public static string NormalizeModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId == LegacyServerModelId)
            return DefaultModelId;
        return modelId.Trim();
    }

    public static bool IsDirectMlOptInEnabled()
    {
        var value = Environment.GetEnvironmentVariable(DirectMlEnvironmentVariable);
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record Qwen3ModelDefinition(
    string Id,
    string DisplayName,
    string RepositoryId,
    string ArchiveFileName,
    string SizeDescription,
    int EstimatedSizeMB,
    bool IsRecommended,
    bool IsQuantized,
    int HiddenSize,
    IReadOnlyList<string> RequiredFiles)
{
    public string ArchiveUrl =>
        $"https://huggingface.co/{RepositoryId}/resolve/main/{ArchiveFileName}";

    public string EncoderFileName => IsQuantized ? "encoder.int4.onnx" : "encoder.onnx";
    public string DecoderInitFileName => IsQuantized ? "decoder_init.int4.onnx" : "decoder_init.onnx";
    public string DecoderStepFileName => IsQuantized ? "decoder_step.int4.onnx" : "decoder_step.onnx";
}
