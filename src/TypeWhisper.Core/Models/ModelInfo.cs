namespace TypeWhisper.Core.Models;

public sealed record ModelFileInfo(string FileName, string DownloadUrl, int EstimatedSizeMB);

public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string SizeDescription { get; init; }
    public required int EstimatedSizeMB { get; init; }
    public required IReadOnlyList<ModelFileInfo> Files { get; init; }
    public string? SubDirectory { get; init; }
    public int LanguageCount { get; init; } = 25;
    public bool SupportsTranslation { get; init; }
    public bool IsRecommended { get; init; }

    private const string ParakeetRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main";
    private const string Canary180MRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/main";

    public static IReadOnlyList<ModelInfo> AvailableModels { get; } =
    [
        new()
        {
            Id = "parakeet-tdt-0.6b",
            DisplayName = "Parakeet TDT 0.6B",
            SizeDescription = "~670 MB",
            EstimatedSizeMB = 670,
            SubDirectory = "parakeet-tdt-0.6b",
            LanguageCount = 25,
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
            SubDirectory = "canary-180m-flash",
            LanguageCount = 4,
            SupportsTranslation = true,
            Files =
            [
                new("encoder.int8.onnx", $"{Canary180MRepo}/encoder.int8.onnx", 127),
                new("decoder.int8.onnx", $"{Canary180MRepo}/decoder.int8.onnx", 71),
                new("tokens.txt", $"{Canary180MRepo}/tokens.txt", 1)
            ]
        }
    ];

    public static ModelInfo? GetById(string id) =>
        AvailableModels.FirstOrDefault(m => m.Id == id);
}
