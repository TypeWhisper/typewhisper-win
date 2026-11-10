namespace TypeWhisper.DiarizationBenchmarks;

/// <summary>Pinned model downloads. Hashes are SHA-256 of the files as served.</summary>
internal static class Pins
{
    public const string SetUrl =
        "https://raw.githubusercontent.com/TypeWhisper/typewhisper-benchmark/seofood/diarization-benchmark/bench/diarization/diarization-set.json";

    public const string NemotronRevision = "52a432672732425e4c19765b9fec9b5810e6f4ba";
    public const string OnnxRevision = "353b6f8ad2cac3580e982d7fbdf0a010786b0406";
    public const string PyannoteRevision = "3533c8cf8e369892e6b79ff1bf80f7b0286a54ee";

    public static readonly (string Url, string RelativePath, string Sha256)[] Files =
    [
        ($"https://huggingface.co/nvidia/Nemotron-3-Diarization/resolve/{NemotronRevision}/Nemotron-3-Diarization.nemo",
            "nvidia-nemotron3/Nemotron-3-Diarization.nemo",
            "867c53f552998f772e5b5e5c082962ae85ee7ca5669c2bc17d7f615133d4e96d"),
        ($"https://huggingface.co/onnx-community/Nemotron-3-Diarization-ONNX/resolve/{OnnxRevision}/onnx/model.onnx",
            "onnx-community-nemotron3/onnx/model.onnx",
            "12a7c98fc1ee6ec98ea728c7ffd9fcf6b245f3c9c1ea9f7c2a7cc47c023e3f9d"),
        ($"https://huggingface.co/onnx-community/Nemotron-3-Diarization-ONNX/resolve/{OnnxRevision}/onnx/model.onnx_data",
            "onnx-community-nemotron3/onnx/model.onnx_data",
            "c293d9b5930eb9f6172f095ced052c0d1bbdbeb2594a115497583ca209b1dbd6"),
        ("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            "sherpa/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            "1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b"),
    ];

    public const string SherpaSegmentationArchive =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";

    public const string SherpaSegmentationSha256 = "d582f4b4c6b48205de7e0643c57df0df5615a3c176189be3fc461e9d18827b5d";
}

/// <summary>
/// Where things live. Everything heavy (audio, models, Python environments, probabilities) is in the
/// data directory outside the repository; the tool directory holds only sources.
/// </summary>
internal sealed record Paths(string Data, string ToolRoot)
{
    public string Manifest => Path.Combine(Data, "diarization-set.json");
    public string Audio => Path.Combine(Data, "audio");
    public string Models => Path.Combine(Data, "models");
    public string Venvs => Path.Combine(Data, "venvs");
    public string Hypotheses => Path.Combine(Data, "hypotheses", "windows");
    public string Probabilities => Path.Combine(Data, "probs");

    public string NemoCheckpoint => Path.Combine(Models, "nvidia-nemotron3", "Nemotron-3-Diarization.nemo");
    public string OnnxModel => Path.Combine(Models, "onnx-community-nemotron3", "onnx", "model.onnx");
    public string PyannoteModel => Path.Combine(Models, "pyannote-community-1");
    public string SherpaSegmentation => Path.Combine(Models, "sherpa", "sherpa-onnx-pyannote-segmentation-3-0", "model.int8.onnx");
    public string SherpaEmbedding => Path.Combine(Models, "sherpa", "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");

    public string Python(string project) => Path.Combine(Venvs, project, "Scripts", "python.exe");

    public string PythonProject(string project) => Path.Combine(ToolRoot, "python", project);

    public string WorkerProject(WorkerKind kind) => Path.Combine(ToolRoot, "Workers", kind switch
    {
        WorkerKind.NemotronOnnxCpu => "NemotronOnnx.Cpu",
        WorkerKind.NemotronOnnxDirectML => "NemotronOnnx.DirectML",
        WorkerKind.Sherpa => "Sherpa",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    });

    public string WorkerExe(WorkerKind kind)
    {
        var project = WorkerProject(kind);
        var name = Path.GetFileName(project) == "Sherpa"
            ? "TypeWhisper.DiarizationBenchmarks.Sherpa"
            : $"TypeWhisper.DiarizationBenchmarks.{Path.GetFileName(project)}";
        return Path.Combine(project, "bin", "Release", "net10.0-windows", name + ".exe");
    }

    /// <summary>Finds tools/TypeWhisper.DiarizationBenchmarks above the runner's output directory.</summary>
    public static string FindToolRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Workers")) && Directory.Exists(Path.Combine(dir.FullName, "python")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("tools/TypeWhisper.DiarizationBenchmarks not found above " + AppContext.BaseDirectory);
    }
}
