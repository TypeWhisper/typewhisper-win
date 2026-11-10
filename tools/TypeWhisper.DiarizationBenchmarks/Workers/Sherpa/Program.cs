using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using SherpaOnnx;
using TypeWhisper.DiarizationBenchmarks.Workers;

// Sherpa ONNX offline speaker diarization: pyannote segmentation 3.0 + 3D-Speaker ERes2Net
// embeddings + fast clustering, on the CPU. Settings left unset keep sherpa-onnx's defaults;
// the load response reports the values the pipeline actually used.
OfflineSpeakerDiarization? diarizer = null;

return WorkerHost.Run(
    config =>
    {
        var threads = config["threads"]?.GetValue<int>() ?? Environment.ProcessorCount;
        var started = Stopwatch.GetTimestamp();
        var options = new OfflineSpeakerDiarizationConfig();
        options.Segmentation.Pyannote.Model = config["segmentation"]!.GetValue<string>();
        options.Segmentation.NumThreads = threads;
        options.Segmentation.Provider = "cpu";
        options.Embedding.Model = config["embedding"]!.GetValue<string>();
        options.Embedding.NumThreads = threads;
        options.Embedding.Provider = "cpu";
        // No speaker-count hint: the clustering threshold decides the count.
        options.Clustering.NumClusters = -1;
        if (config["threshold"] is { } threshold)
        {
            options.Clustering.Threshold = threshold.GetValue<float>();
        }

        diarizer = new OfflineSpeakerDiarization(options);
        var version = typeof(OfflineSpeakerDiarization).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(OfflineSpeakerDiarization).Assembly.GetName().Version?.ToString();
        return new JsonObject
        {
            ["modelLoadMs"] = WorkerHost.ElapsedMs(started),
            ["runtime"] = $"sherpa-onnx {version?.Split('+')[0]}",
            ["settings"] = new JsonObject
            {
                ["clusteringThreshold"] = options.Clustering.Threshold,
                ["numClusters"] = options.Clustering.NumClusters,
                ["minDurationOn"] = options.MinDurationOn,
                ["minDurationOff"] = options.MinDurationOff,
                ["windowShiftRatio"] = options.Segmentation.Pyannote.WindowShiftRatio,
                ["threads"] = threads,
                ["executionProvider"] = "cpu",
            },
        };
    },
    (path, _) =>
    {
        var started = Stopwatch.GetTimestamp();
        var samples = WavFile.ReadMono16k(path);
        var segments = diarizer!.Process(samples);
        var turns = WorkerHost.TurnsToJson(segments.Select(s => ((double)s.Start, (double)s.End, $"S{s.Speaker}")));
        return new JsonObject { ["turns"] = turns, ["durationMs"] = WorkerHost.ElapsedMs(started) };
    });
