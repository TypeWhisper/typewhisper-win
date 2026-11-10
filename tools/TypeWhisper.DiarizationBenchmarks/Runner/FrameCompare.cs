using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace TypeWhisper.DiarizationBenchmarks;

/// <summary>
/// Checks a port against the NeMo reference before a full run: every engine diarizes the same files
/// and dumps its 10 ms speaker probabilities, then each candidate is compared frame by frame with
/// the reference of the same profile.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FrameCompare
{
    public static readonly (string Reference, string Candidate)[] Pairs =
    [
        ("nemotron3-nemo-fast32", "nemotron3-onnx-cpu-fast32"),
        ("nemotron3-nemo-fast32", "nemotron3-onnx-dml-fast32"),
        ("nemotron3-nemo-offline", "nemotron3-onnx-cpu-offline"),
        ("nemotron3-nemo-offline", "nemotron3-onnx-dml-offline"),
    ];

    public static async Task<JsonObject> RunAsync(Paths paths, IReadOnlyList<SetItem> items, IReadOnlyList<(string Reference, string Candidate)> pairs)
    {
        var engineIds = pairs.SelectMany(p => new[] { p.Reference, p.Candidate }).Distinct().ToList();
        var turnsByEngine = new Dictionary<(string, string), JsonArray>();
        foreach (var id in engineIds)
        {
            var engine = Engines.All.Single(e => e.Id == id);
            var directory = Path.Combine(paths.Probabilities, id);
            Directory.CreateDirectory(directory);
            Console.WriteLine($"{id}: loading");
            using var worker = WorkerProcess.Start(engine, paths);
            var load = await worker.RequestAsync(new JsonObject { ["op"] = "load", ["config"] = engine.Config(paths) }, TimeSpan.FromMinutes(30));
            if (load.Response["error"] is { } error)
            {
                throw new InvalidOperationException($"{id} failed to load: {error}");
            }

            foreach (var item in items)
            {
                var result = await worker.RequestAsync(
                    new JsonObject
                    {
                        ["op"] = "diarize",
                        ["path"] = Path.Combine(paths.Audio, item.Audio),
                        ["probs"] = ProbsPath(paths, id, item),
                    },
                    TimeSpan.FromSeconds(Math.Max(600, item.Seconds * 3)));
                if (result.Response["error"] is { } diarizeError)
                {
                    throw new InvalidOperationException($"{id} failed on {item.Id}: {diarizeError}");
                }

                turnsByEngine[(id, item.Id)] = result.Response["turns"]!.AsArray();
                Console.WriteLine($"  {item.Id}: {result.Response["durationMs"]!.GetValue<double>() / 1000:F1} s");
            }
        }

        var report = new JsonObject
        {
            ["note"] = "Speaker probabilities per 10 ms frame and speaker; agreement counts cells on the same side of 0.5.",
            ["pairs"] = new JsonArray(),
        };
        foreach (var (reference, candidate) in pairs)
        {
            var files = new JsonArray();
            long cells = 0, agreeing = 0, frames = 0, agreeingFrames = 0;
            double sumAbs = 0, maxAbs = 0;
            foreach (var item in items)
            {
                var a = Read(ProbsPath(paths, reference, item));
                var b = Read(ProbsPath(paths, candidate, item));
                var n = Math.Min(a.Length, b.Length) / 8;
                long fileAgree = 0, fileAgreeFrames = 0;
                double fileSum = 0, fileMax = 0;
                for (var f = 0; f < n; f++)
                {
                    var frameAgrees = true;
                    for (var s = 0; s < 8; s++)
                    {
                        var x = a[(f * 8) + s];
                        var y = b[(f * 8) + s];
                        var d = Math.Abs(x - y);
                        fileSum += d;
                        fileMax = Math.Max(fileMax, d);
                        if ((x > 0.5f) == (y > 0.5f))
                        {
                            fileAgree++;
                        }
                        else
                        {
                            frameAgrees = false;
                        }
                    }

                    if (frameAgrees)
                    {
                        fileAgreeFrames++;
                    }
                }

                cells += n * 8L;
                agreeing += fileAgree;
                frames += n;
                agreeingFrames += fileAgreeFrames;
                sumAbs += fileSum;
                maxAbs = Math.Max(maxAbs, fileMax);
                files.Add(new JsonObject
                {
                    ["id"] = item.Id,
                    ["referenceFrames"] = a.Length / 8,
                    ["candidateFrames"] = b.Length / 8,
                    ["meanAbsDiff"] = fileSum / Math.Max(1, n * 8),
                    ["maxAbsDiff"] = fileMax,
                    ["cellAgreement"] = (double)fileAgree / Math.Max(1, n * 8),
                    ["frameAgreement"] = (double)fileAgreeFrames / Math.Max(1, n),
                    ["referenceSpeakers"] = Speakers(turnsByEngine[(reference, item.Id)]),
                    ["candidateSpeakers"] = Speakers(turnsByEngine[(candidate, item.Id)]),
                });
            }

            report["pairs"]!.AsArray().Add(new JsonObject
            {
                ["reference"] = reference,
                ["candidate"] = candidate,
                ["frames"] = frames,
                ["meanAbsDiff"] = sumAbs / Math.Max(1, cells),
                ["maxAbsDiff"] = maxAbs,
                ["cellAgreement"] = (double)agreeing / Math.Max(1, cells),
                ["frameAgreement"] = (double)agreeingFrames / Math.Max(1, frames),
                ["files"] = files,
            });
            Console.WriteLine(Runner.Invariant($"{candidate} vs {reference}: {frames} frames, mean |Δp| {sumAbs / Math.Max(1, cells):E2}, max {maxAbs:F3}, cells agreeing {100.0 * agreeing / Math.Max(1, cells):F3} %, frames agreeing {100.0 * agreeingFrames / Math.Max(1, frames):F3} %"));
            foreach (var file in files)
            {
                Console.WriteLine(Runner.Invariant($"  {file!["id"]}: frames agreeing {100 * file["frameAgreement"]!.GetValue<double>():F3} %, max |Δp| {file["maxAbsDiff"]!.GetValue<double>():F3}, speakers {file["referenceSpeakers"]} vs {file["candidateSpeakers"]}"));
            }
        }

        return report;
    }

    private static int Speakers(JsonArray turns) => turns.Select(t => t![2]!.ToString()).Distinct().Count();

    private static string ProbsPath(Paths paths, string engineId, SetItem item) =>
        Path.Combine(paths.Probabilities, engineId, item.Id.Replace('/', '_') + ".f32");

    private static float[] Read(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();
}
