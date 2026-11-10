using System.Text.Json.Nodes;

namespace TypeWhisper.DiarizationBenchmarks;

internal enum WorkerKind
{
    Nemo,
    Pyannote,
    NemotronOnnxCpu,
    NemotronOnnxDirectML,
    Sherpa,
}

/// <summary>One engine configuration: a worker plus the load request it gets.</summary>
internal sealed record Engine(
    string Id,
    string Name,
    WorkerKind Worker,
    string Model,
    bool UsesGpu,
    Func<Paths, JsonObject> Config,
    Dictionary<string, object?> Settings);

internal static class Engines
{
    /// <summary>Physical cores of the Ryzen 7 7800X3D; CPU engines use one thread per core.</summary>
    public static int CpuThreads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    private static Dictionary<string, object?> NemotronSettings(string profile, string note) => new()
    {
        ["profile"] = profile,
        ["threshold"] = 0.5,
        ["minTurnSeconds"] = 0.2,
        ["note"] = note,
    };

    private const string Fast32Note = "FluidAudio fast32: chunk 32, right context 4, FIFO 40, speaker cache 264, update period 40 (2.88 s latency)";
    private const string OfflineNote = "model card offline: chunk 340, right context 40, FIFO 40, speaker cache 264, update period 300 (30.4 s latency)";

    private static Engine Nemo(string profile, string note) => new(
        $"nemotron3-nemo-{profile}",
        $"Nemotron 3 Diarization, NeMo reference ({profile})",
        WorkerKind.Nemo,
        $"nvidia/Nemotron-3-Diarization@{Pins.NemotronRevision[..8]} Nemotron-3-Diarization.nemo, fp32",
        UsesGpu: true,
        paths => new JsonObject { ["model"] = paths.NemoCheckpoint, ["profile"] = profile, ["device"] = "cuda" },
        new(NemotronSettings(profile, note))
        {
            ["executionProvider"] = "cuda",
            ["attention"] = "scaled_dot_product_attention (NeMo's export path; FlexAttention needs Triton, which PyTorch lacks on Windows)",
        });

    private static Engine Onnx(string provider, string profile, string note) => new(
        $"nemotron3-onnx-{provider}-{profile}",
        $"Nemotron 3 Diarization, ONNX Runtime {(provider == "dml" ? "DirectML" : "CPU")} ({profile})",
        provider == "dml" ? WorkerKind.NemotronOnnxDirectML : WorkerKind.NemotronOnnxCpu,
        $"onnx-community/Nemotron-3-Diarization-ONNX@{Pins.OnnxRevision[..8]} onnx/model.onnx, fp32",
        UsesGpu: provider == "dml",
        paths => new JsonObject
        {
            ["model"] = paths.OnnxModel,
            ["provider"] = provider,
            ["profile"] = profile,
            ["threads"] = CpuThreads,
        },
        new(NemotronSettings(profile, note))
        {
            ["executionProvider"] = provider == "dml" ? "directml" : "cpu",
            ["streamingState"] = "C# port of Transformers' Nemotron3DiarizationSpeakerCache; mel front end in C#",
        });

    public static readonly Engine[] All =
    [
        Nemo("offline", OfflineNote),
        Nemo("fast32", Fast32Note),
        Onnx("dml", "fast32", Fast32Note),
        Onnx("cpu", "fast32", Fast32Note),
        Onnx("dml", "offline", OfflineNote),
        Onnx("cpu", "offline", OfflineNote),
        new(
            "pyannote-community1-cuda",
            "pyannote speaker-diarization-community-1",
            WorkerKind.Pyannote,
            $"pyannote/speaker-diarization-community-1@{Pins.PyannoteRevision[..8]}",
            UsesGpu: true,
            paths => new JsonObject { ["model"] = paths.PyannoteModel, ["device"] = "cuda" },
            new() { ["executionProvider"] = "cuda", ["numSpeakers"] = null }),
        new(
            "sherpa-onnx-eres2net-cpu",
            "Sherpa ONNX pyannote segmentation + ERes2Net",
            WorkerKind.Sherpa,
            "sherpa-onnx-pyannote-segmentation-3-0 model.int8.onnx + 3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            UsesGpu: false,
            paths => new JsonObject
            {
                ["segmentation"] = paths.SherpaSegmentation,
                ["embedding"] = paths.SherpaEmbedding,
                ["threads"] = CpuThreads,
            },
            new()),
    ];

    /// <summary>The engines a plain <c>run</c> measures; the ONNX offline profiles are extra.</summary>
    public static readonly string[] Default =
    [
        "nemotron3-nemo-offline",
        "nemotron3-nemo-fast32",
        "nemotron3-onnx-dml-fast32",
        "nemotron3-onnx-cpu-fast32",
        "pyannote-community1-cuda",
        "sherpa-onnx-eres2net-cpu",
    ];
}
