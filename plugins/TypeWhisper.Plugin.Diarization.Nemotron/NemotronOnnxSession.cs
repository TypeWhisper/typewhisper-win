using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>The exported Nemotron graph in an ONNX Runtime session on the CPU or a DirectML adapter.</summary>
internal sealed class NemotronOnnxSession : INemotronStep, IDisposable
{
    private static readonly string[] InputNames = ["input_features", "cached_embeds", "attention_mask"];
    private static readonly string[] OutputNames = ["logits", "chunk_embeds", "silence_embeds"];
    private static readonly object DirectMlLoad = new();
    private static bool _directMlLoaded;
    private readonly InferenceSession _session;

    private NemotronOnnxSession(InferenceSession session, NemotronProfile profile, bool fixedShapes, string device)
    {
        _session = session;
        Profile = profile;
        FixedShapes = fixedShapes;
        Device = device;
    }

    internal NemotronProfile Profile { get; }
    internal bool FixedShapes { get; }
    internal string Device { get; }

    /// <summary>Opens the CPU graph, or with an adapter the DirectML graph whose Gelu nodes run on the GPU.</summary>
    /// <param name="cpuModel">The exported model.onnx.</param>
    /// <param name="directMlModel">The rewritten model.dml.onnx, used with an adapter.</param>
    /// <param name="profile">The streaming profile; DirectML fixes the input shapes to it.</param>
    /// <param name="adapter">The DirectML adapter, or null for the CPU.</param>
    internal static NemotronOnnxSession Open(string cpuModel, string directMlModel, NemotronProfile profile, DirectMlAdapter? adapter)
    {
        // The session keeps what it needs from the options, so they go when this method returns.
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        string model, device;
        var fixedShapes = adapter is not null;
        if (adapter is { } gpu)
        {
            LoadDirectMl();
            // DirectML needs sequential execution without memory pattern optimization.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            // With fixed shapes DirectML fuses the whole graph, and the fused graph of this export returns
            // probabilities as if there were no speaker cache. Unfused, the results match the CPU provider and
            // the fixed shapes still save most of the per-step overhead.
            options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
            options.AppendExecutionProvider_DML(gpu.Index);
            // Fixed input shapes let DirectML compile the graph once; with free dimensions it runs every operator
            // on its own.
            var cachedFrames = profile.SpeakerCacheLength + profile.FifoLength;
            var stepFrames = profile.ChunkLength + profile.RightContext;
            options.AddFreeDimensionOverrideByName("batch_size", 1);
            options.AddFreeDimensionOverrideByName("num_mel_frames", stepFrames * NemotronStreamingDiarizer.Subsampling);
            options.AddFreeDimensionOverrideByName("num_cached_frames", cachedFrames);
            options.AddFreeDimensionOverrideByName("num_frames", cachedFrames + stepFrames);
            model = directMlModel;
            device = gpu.Name;
        }
        else
        {
            model = cpuModel;
            device = "CPU";
        }

        var session = new InferenceSession(model, options);
        return new(session, profile, fixedShapes, device);
    }

    public NemotronStepOutput Run(float[] features, int melFrames, float[] cached, int cachedFrames, long[] mask, CancellationToken cancellationToken)
    {
        // Held for the whole call: a RunOptions created inline can be finalized while the native call still uses it.
        using var runOptions = new RunOptions();
        using var stop = cancellationToken.Register(() => runOptions.Terminate = true);
        using var inputFeatures = OrtValue.CreateTensorValueFromMemory(features, [1, melFrames, MelFrontend.Mels]);
        using var cachedEmbeds = OrtValue.CreateTensorValueFromMemory(cached, [1, cachedFrames, NemotronStreamingDiarizer.Hidden]);
        using var attentionMask = OrtValue.CreateTensorValueFromMemory(mask, [1, mask.Length]);
        try
        {
            using var results = _session.Run(runOptions, InputNames, [inputFeatures, cachedEmbeds, attentionMask], OutputNames);
            return new(results[0].GetTensorDataAsSpan<float>().ToArray(), results[1].GetTensorDataAsSpan<float>().ToArray(),
                results[2].GetTensorDataAsSpan<float>().ToArray());
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose() => _session.Dispose();

    // ONNX Runtime loads DirectML.dll by name, which would find the older copy in System32. The package ships the
    // version its ONNX Runtime needs next to onnxruntime.dll; loading it first by path makes later loads by name use it.
    private static void LoadDirectMl()
    {
        lock (DirectMlLoad)
        {
            if (_directMlLoaded) return;
            var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            var directory = Path.GetDirectoryName(typeof(NemotronOnnxSession).Assembly.Location)!;
            var path = Path.Join(directory, "runtimes", architecture, "native", "DirectML.dll");
            if (File.Exists(path)) NativeLibrary.Load(path);
            _directMlLoaded = true;
        }
    }
}
