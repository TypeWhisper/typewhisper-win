using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.VoxtralLocal;

/// <summary>A loaded Voxtral model; the plugin serializes calls.</summary>
internal interface IVoxtralRecognizer : IDisposable
{
    /// <summary>Where the model runs, as shown in the processing device settings.</summary>
    TranscriptionAccelerationStatus Status { get; }
    string Transcribe(float[] samples, CancellationToken cancellationToken);
}

/// <summary>Runs the GGUF model through transcribe.cpp on the graphics card with Vulkan, or on the processor.</summary>
internal sealed class TranscribeCppVoxtral(TranscribeCppSession session, TranscriptionAccelerationStatus status) : IVoxtralRecognizer
{
    internal const string SlowOnProcessor =
        "Voxtral needs a graphics card for dictation. On the processor, a sentence takes about as long to transcribe as to speak.";
    internal const string SlowOnIntegratedGraphics =
        "Voxtral runs on integrated graphics, which can take about as long as speaking. A dedicated graphics card is much faster.";

    public TranscriptionAccelerationStatus Status { get; } = status;

    /// <summary>
    /// Loads the model. Automatic and GPU place it on the first dedicated graphics card with Vulkan, otherwise on
    /// integrated graphics; Automatic falls back to the processor when no graphics card can load it. The processor
    /// choice does not register Vulkan at all, so a broken graphics driver cannot affect it.
    /// </summary>
    internal static TranscribeCppVoxtral Open(string modelPath, string runtimeDirectory, TranscriptionAccelerationPreference preference,
        Action<int, string>? log, Action<string>? warn)
    {
        var useGpu = preference != TranscriptionAccelerationPreference.Cpu;
        // ggml registers its backends once per process. A process that already started keeps its set: one with Vulkan
        // can still run on the processor, one without it needs a restart for the graphics card (the host starts a new
        // worker for a changed device, so this only matters when the engine runs in the app process).
        var mask = TranscribeCppRuntime.IsInitialized ? TranscribeCppRuntime.AllowedBackends
            : useGpu ? TranscribeCppNative.BackendMaskCpu | TranscribeCppNative.BackendMaskVulkan : TranscribeCppNative.BackendMaskCpu;
        TranscribeCppRuntime.Initialize(runtimeDirectory, mask, log);
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        string? gpuProblem = null;
        if (useGpu)
        {
            var vulkan = (mask & TranscribeCppNative.BackendMaskVulkan) != 0;
            var device = vulkan ? TranscribeCppRuntime.PreferredVulkanDevice(TranscribeCppRuntime.Devices()) : null;
            if (!vulkan)
            {
                gpuProblem = "Restart TypeWhisper to use the graphics card.";
                if (preference == TranscriptionAccelerationPreference.AmdVulkan) throw new InvalidOperationException(gpuProblem);
            }
            else if (device is null)
            {
                gpuProblem = "No graphics card with Vulkan support was found.";
                if (preference == TranscriptionAccelerationPreference.AmdVulkan)
                    throw new InvalidOperationException(gpuProblem + " Update the graphics driver or choose CPU.");
            }
            else
            {
                try
                {
                    var session = TranscribeCppSession.Open(modelPath, TranscribeCppNative.BackendVulkan, device, threads);
                    return new(session, GpuStatus(session.Device ?? device));
                }
                catch (Exception ex) when (preference == TranscriptionAccelerationPreference.Auto && ex is not OperationCanceledException)
                {
                    gpuProblem = "The graphics card could not load Voxtral: " + ex.Message;
                }
            }
            warn?.Invoke(gpuProblem + " Using the processor.");
        }
        return new(TranscribeCppSession.Open(modelPath, TranscribeCppNative.BackendCpu, null, threads), CpuStatus(gpuProblem));
    }

    internal static TranscriptionAccelerationStatus GpuStatus(TranscribeCppDevice device) => device.Kind == "vulkan"
        ? new(TranscriptionAccelerationBackend.AmdVulkan, device.Description, device.IsIntegrated ? SlowOnIntegratedGraphics : null)
        : CpuStatus(null);

    internal static TranscriptionAccelerationStatus CpuStatus(string? gpuProblem) =>
        new(TranscriptionAccelerationBackend.Cpu, "Using CPU", gpuProblem is null ? SlowOnProcessor : gpuProblem + " " + SlowOnProcessor);

    // Voxtral Realtime detects the language itself; its streaming decoder has no language hint, and the offline
    // decoder is kept on the same footing.
    public string Transcribe(float[] samples, CancellationToken cancellationToken) =>
        session.Transcribe(samples, null, cancellationToken);

    public void Dispose() => session.Dispose();
}
