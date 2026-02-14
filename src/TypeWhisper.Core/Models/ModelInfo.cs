namespace TypeWhisper.Core.Models;

public enum EngineType
{
    Whisper,
    Parakeet
}

public sealed record ModelFileInfo(string FileName, string DownloadUrl, int EstimatedSizeMB);

public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SizeDescription { get; init; }
    public required int EstimatedSizeMB { get; init; }
    public required IReadOnlyList<ModelFileInfo> Files { get; init; }
    public EngineType Engine { get; init; } = EngineType.Whisper;
    public string? SubDirectory { get; init; }
    public int LanguageCount { get; init; } = 99;
    public bool SupportsTranslation { get; init; } = true;
    public bool IsRecommended { get; init; }

    private const string ParakeetRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main";
    private const string Canary180MRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/main";
    private const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    public static IReadOnlyList<ModelInfo> AvailableModels { get; } =
    [
        // --- Parakeet (NVIDIA) ---
        new()
        {
            Id = "parakeet-tdt-0.6b",
            DisplayName = "Parakeet TDT 0.6B",
            SizeDescription = "~670 MB",
            EstimatedSizeMB = 670,
            Engine = EngineType.Parakeet,
            SubDirectory = "parakeet-tdt-0.6b",
            LanguageCount = 25,
            SupportsTranslation = false,
            IsRecommended = true,
            Files =
            [
                new("encoder.int8.onnx", $"{ParakeetRepo}/encoder.int8.onnx", 652),
                new("decoder.int8.onnx", $"{ParakeetRepo}/decoder.int8.onnx", 12),
                new("joiner.int8.onnx", $"{ParakeetRepo}/joiner.int8.onnx", 6),
                new("tokens.txt", $"{ParakeetRepo}/tokens.txt", 1)
            ]
        },
        new()
        {
            Id = "canary-180m-flash",
            DisplayName = "Canary 180M Flash",
            SizeDescription = "~198 MB",
            EstimatedSizeMB = 198,
            Engine = EngineType.Parakeet,
            SubDirectory = "canary-180m-flash",
            LanguageCount = 4,
            SupportsTranslation = true,
            Files =
            [
                new("encoder.int8.onnx", $"{Canary180MRepo}/encoder.int8.onnx", 127),
                new("decoder.int8.onnx", $"{Canary180MRepo}/decoder.int8.onnx", 71),
                new("tokens.txt", $"{Canary180MRepo}/tokens.txt", 1)
            ]
        },

        // --- Whisper (OpenAI) ---
        new()
        {
            Id = "tiny",
            DisplayName = "Whisper Tiny",
            SizeDescription = "~75 MB",
            EstimatedSizeMB = 75,
            Files = [new("ggml-tiny.bin", $"{WhisperRepo}/ggml-tiny.bin", 75)]
        },
        new()
        {
            Id = "base",
            DisplayName = "Whisper Base",
            SizeDescription = "~142 MB",
            EstimatedSizeMB = 142,
            Files = [new("ggml-base.bin", $"{WhisperRepo}/ggml-base.bin", 142)]
        },
        new()
        {
            Id = "small",
            DisplayName = "Whisper Small",
            SizeDescription = "~466 MB",
            EstimatedSizeMB = 466,
            Files = [new("ggml-small.bin", $"{WhisperRepo}/ggml-small.bin", 466)]
        },
        new()
        {
            Id = "medium",
            DisplayName = "Whisper Medium",
            SizeDescription = "~1.5 GB",
            EstimatedSizeMB = 1500,
            Files = [new("ggml-medium.bin", $"{WhisperRepo}/ggml-medium.bin", 1500)]
        },
        new()
        {
            Id = "large-v3",
            DisplayName = "Whisper Large v3",
            SizeDescription = "~3.1 GB",
            EstimatedSizeMB = 3100,
            Files = [new("ggml-large-v3.bin", $"{WhisperRepo}/ggml-large-v3.bin", 3100)]
        },
        new()
        {
            Id = "large-v3-turbo",
            DisplayName = "Whisper Large v3 Turbo",
            SizeDescription = "~1.6 GB",
            EstimatedSizeMB = 1600,
            Files = [new("ggml-large-v3-turbo.bin", $"{WhisperRepo}/ggml-large-v3-turbo.bin", 1600)]
        }
    ];

    public static ModelInfo? GetById(string id) =>
        AvailableModels.FirstOrDefault(m => m.Id == id);
}
