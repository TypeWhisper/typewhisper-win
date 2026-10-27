using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>The GGUF files the graphics card path runs; the shared runtime asset is <see cref="TranscribeCppAsset.Runtime"/>.</summary>
internal static class ParakeetGguf
{
    // The transcribe.cpp project's GGUF conversion of nvidia/parakeet-tdt-0.6b-v3, pinned to commit 90f08245 of
    // 2026-09-15; the hash is the LFS object id from the Hugging Face tree API and matched a download.
    internal static readonly TranscribeCppAsset ParakeetV3Q8 = new("Parakeet TDT 0.6B v3 for the GPU",
        "https://huggingface.co/handy-computer/parakeet-tdt-0.6b-v3-gguf/resolve/90f082450fcbacdb54e5900c44ef697c9ea59622/parakeet-tdt-0.6b-v3-Q8_0.gguf",
        "5859f77944efcd8eafa23a6350731960b2b55b2203df51f319665c807d802cc7", 739508576, "parakeet-tdt-0.6b-v3-Q8_0.gguf");

    // Our conversion of the same Parakeet Ultra checkpoint as the ONNX export (Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx,
    // .nemo SHA-256 91b81b3c…35da) with transcribe.cpp 0.3.1's convert-parakeet.py and transcribe-quantize --quant Q8_0.
    internal static readonly TranscribeCppAsset ParakeetUltraQ8 = new("Parakeet Ultra 0.6B for the GPU",
        "https://github.com/TypeWhisper/typewhisper-win/releases/download/model-parakeet-ultra-gguf-v1/parakeet-ultra-Q8_0.gguf",
        "b8752062d51d5d24f6e1355a0b5973d9a47f0d9cd6db8c29385c191ff6f5dcd9", 739508704, "parakeet-ultra-Q8_0.gguf");
}

/// <summary>A Parakeet model loaded on the graphics card; the plugin serializes calls.</summary>
internal interface IGpuRecognizer : IDisposable
{
    /// <summary>Where the model runs, as shown in the processing device settings.</summary>
    TranscriptionAccelerationStatus Status { get; }
    TranscribeCppResult Transcribe(float[] samples, bool tokens, CancellationToken cancellationToken);
}

internal sealed class TranscribeCppRecognizer(TranscribeCppSession session) : IGpuRecognizer
{
    public TranscriptionAccelerationStatus Status { get; } = DescribeDevice(session.Device);

    // Parakeet v3 detects the language itself; transcribe.cpp would accept a hint but ignores it for Parakeet.
    public TranscribeCppResult Transcribe(float[] samples, bool tokens, CancellationToken cancellationToken) =>
        session.Transcribe(samples, null, tokens, cancellationToken);

    internal static TranscriptionAccelerationStatus DescribeDevice(TranscribeCppDevice? device) => device is { Kind: "vulkan" }
        ? new(TranscriptionAccelerationBackend.AmdVulkan, device.Description)
        : new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    public void Dispose() => session.Dispose();
}
