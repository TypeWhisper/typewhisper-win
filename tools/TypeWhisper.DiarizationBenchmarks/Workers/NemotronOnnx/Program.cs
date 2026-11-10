using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using TypeWhisper.DiarizationBenchmarks.Workers;

// Nemotron 3 Diarization through ONNX Runtime from C#. Frame probabilities become turns the way
// FluidAudio does it: active above 0.5 per 10 ms frame, turns shorter than 0.2 s dropped.
const float Threshold = 0.5f;
const double MinTurnSeconds = 0.2;
const double FrameSeconds = 0.01;

InferenceSession? session = null;
NemotronStreamingDiarizer? diarizer = null;

return WorkerHost.Run(
    config =>
    {
        var provider = config["provider"]?.GetValue<string>() ?? "cpu";
        var profile = config["profile"]?.GetValue<string>() switch
        {
            "offline" => NemotronProfile.Offline,
            "fast32" or null => NemotronProfile.Fast32,
            var other => throw new ArgumentException($"unknown profile {other}"),
        };
        var info = new JsonObject();
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (provider == "dml")
        {
#if ORT_DIRECTML
            var adapters = DxgiAdapters.List();
            var wanted = config["adapter"]?.GetValue<string>() ?? "NVIDIA";
            var adapter = adapters.FirstOrDefault(a => !a.Software && a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (adapter.Name is null)
            {
                throw new InvalidOperationException($"no DXGI adapter matching {wanted}: {string.Join(", ", adapters.Select(a => a.Name))}");
            }

            // DirectML needs sequential execution without memory pattern optimization.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(adapter.Index);
            info["device"] = $"{adapter.Name} (DXGI adapter {adapter.Index}, {adapter.DedicatedMb} MB)";
#else
            throw new InvalidOperationException("this build has no DirectML provider; build with -p:OrtFlavor=DirectML");
#endif
        }
        else
        {
            if (config["threads"] is { } threads)
            {
                options.IntraOpNumThreads = threads.GetValue<int>();
            }

            info["device"] = "cpu";
        }

        var started = Stopwatch.GetTimestamp();
        session = new InferenceSession(config["model"]!.GetValue<string>(), options);
        diarizer = new NemotronStreamingDiarizer(session, profile);
        info["modelLoadMs"] = WorkerHost.ElapsedMs(started);
        info["runtime"] = $"ONNX Runtime {OrtEnv.Instance().GetVersionString()} ({(provider == "dml" ? "DirectML" : "CPU")})";
        info["intraOpThreads"] = options.IntraOpNumThreads;
        info["profile"] = new JsonObject
        {
            ["chunk_len"] = profile.ChunkLength,
            ["chunk_right_context"] = profile.RightContext,
            ["fifo_len"] = profile.FifoLength,
            ["spkcache_len"] = profile.SpeakerCacheLength,
            ["spkcache_update_period"] = profile.UpdatePeriod,
        };
        return info;
    },
    (path, probsPath) =>
    {
        var started = Stopwatch.GetTimestamp();
        var audio = WavFile.ReadMono16k(path);
        var features = MelFrontend.Compute(audio, out var frames, out var validFrames);
        var probs = diarizer!.Run(features, frames, validFrames);
        var turns = WorkerHost.TurnsToJson(Turns(probs));
        var durationMs = WorkerHost.ElapsedMs(started);
        if (probsPath is not null)
        {
            using var file = File.Create(probsPath);
            file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(probs.AsSpan()));
        }

        return new JsonObject { ["turns"] = turns, ["durationMs"] = durationMs, ["frames"] = probs.Length / 8 };
    });

static IEnumerable<(double, double, string)> Turns(float[] probs)
{
    var frames = probs.Length / 8;
    for (var s = 0; s < 8; s++)
    {
        var start = -1;
        for (var f = 0; f <= frames; f++)
        {
            var active = f < frames && probs[(f * 8) + s] > Threshold;
            if (active && start < 0)
            {
                start = f;
            }
            else if (!active && start >= 0)
            {
                if ((f - start) * FrameSeconds >= MinTurnSeconds - 1e-9)
                {
                    yield return (start * FrameSeconds, f * FrameSeconds, $"S{s}");
                }

                start = -1;
            }
        }
    }
}
