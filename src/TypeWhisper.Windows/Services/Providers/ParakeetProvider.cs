using System.IO;
using SherpaOnnx;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services.Providers;

public sealed class ParakeetProvider : LocalProviderBase
{
    private const string Repo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main";

    public override string Id => "parakeet-tdt-0.6b";
    public override string DisplayName => "Parakeet TDT 0.6B";

    public override ModelInfo Model { get; } = new()
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
            new("encoder.int8.onnx", $"{Repo}/encoder.int8.onnx", 652),
            new("decoder.int8.onnx", $"{Repo}/decoder.int8.onnx", 12),
            new("joiner.int8.onnx", $"{Repo}/joiner.int8.onnx", 6),
            new("tokens.txt", $"{Repo}/tokens.txt", 1)
        ]
    };

    protected override OfflineRecognizer CreateRecognizer(string modelDir)
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
}
