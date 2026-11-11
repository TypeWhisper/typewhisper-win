using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeWhisper.DiarizationBenchmarks;

/// <summary>
/// Runs engines over the set, one engine loaded at a time and one file at a time, and writes
/// hypotheses/windows/&lt;engineId&gt;.json after every file, so an interrupted run resumes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Runner
{
    public static async Task<List<string>> RunAsync(Paths paths, BenchmarkSet set, IReadOnlyList<Engine> engines, IReadOnlyList<SetItem> items, bool resume)
    {
        Directory.CreateDirectory(paths.Hypotheses);
        var written = new List<string>();
        foreach (var engine in engines)
        {
            var path = Path.Combine(paths.Hypotheses, engine.Id + ".json");
            var machine = Machine.Describe(engine.UsesGpu ? Machine.Gpu() : null);
            var results = new Dictionary<string, JsonObject>();
            JsonObject? earlier = null;
            if (resume && File.Exists(path))
            {
                earlier = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                if (earlier["setId"]?.GetValue<string>() != set.Id || earlier["model"]?.GetValue<string>() != engine.Model
                    || !JsonNode.DeepEquals(earlier["machine"], machine))
                {
                    throw new InvalidOperationException($"{path} was measured with another set, model or machine; run without --resume");
                }

                foreach (var item in earlier["items"]!.AsArray().Select(i => i!.AsObject()).Where(i => i["error"] is null))
                {
                    results[item["id"]!.GetValue<string>()] = (JsonObject)item.DeepClone();
                }
            }

            var todo = items.Where(i => !results.ContainsKey(i.Id)).ToList();
            if (todo.Count == 0)
            {
                Console.WriteLine($"{engine.Id}: all {items.Count} files done");
                written.Add(path);
                continue;
            }

            Console.WriteLine($"{engine.Id}: loading");
            var processStarted = Stopwatch.GetTimestamp();
            var worker = WorkerProcess.Start(engine, paths);
            var settings = new Dictionary<string, object?>(engine.Settings);
            var gpuPeak = 0.0;
            var torchAllocatedPeak = 0.0;
            var torchReservedPeak = 0.0;
            try
            {
                var load = await worker.RequestAsync(new JsonObject { ["op"] = "load", ["config"] = engine.Config(paths) }, TimeSpan.FromMinutes(30));
                if (load.Response["error"] is { } loadError)
                {
                    throw new InvalidOperationException($"{engine.Id} failed to load: {loadError}");
                }

                // From starting the worker to the loaded model: interpreter or runtime start, imports, model load.
                var prepareMs = Stopwatch.GetElapsedTime(processStarted).TotalMilliseconds;
                var info = load.Response;
                var runtime = info["runtime"]?.GetValue<string>() ?? "unknown";
                FlattenInto(settings, info, ["ok", "runtime", "modelLoadMs", "pid"]);
                settings["modelLoadMs"] = Round(info["modelLoadMs"]?.GetValue<double>() ?? 0, 1);
                settings["prepareIncludes"] = "worker process start, runtime import, model load; no download, no warm-up";
                Console.WriteLine($"  {runtime}, prepare {prepareMs / 1000:F1} s");
                // Earlier results only join a run with the same runtime and effective settings (threads,
                // profile, provider); the measured values in settings may differ.
                if (earlier is not null
                    && (earlier["runtime"]?.GetValue<string>() != runtime
                        // Through JSON text, so numbers compare the way the file stores them.
                        || !JsonNode.DeepEquals(
                            Configuration(earlier["settings"]?.AsObject()),
                            Configuration(JsonNode.Parse(ToJson(settings).ToJsonString())!.AsObject()))))
                {
                    throw new InvalidOperationException($"{path} was measured with another runtime or other settings; run without --resume");
                }

                foreach (var item in todo)
                {
                    var timeout = TimeSpan.FromSeconds(Math.Max(600, item.Seconds * 3));
                    JsonObject entry;
                    try
                    {
                        var result = await worker.RequestAsync(
                            new JsonObject { ["op"] = "diarize", ["path"] = Path.Combine(paths.Audio, item.Audio) },
                            timeout);
                        var response = result.Response;
                        if (response["error"] is { } error || response["turns"] is not JsonArray)
                        {
                            entry = Failed(item, response["error"]?.ToString() ?? "no turns");
                        }
                        else
                        {
                            var turns = new JsonArray();
                            foreach (var turn in response["turns"]!.AsArray())
                            {
                                var t = turn!.AsArray();
                                turns.Add(new JsonArray(Round(t[0]!.GetValue<double>()), Round(t[1]!.GetValue<double>()), t[2]!.ToString()));
                            }

                            var durationMs = response["durationMs"]?.GetValue<double>() ?? result.WallMs;
                            entry = new JsonObject
                            {
                                ["id"] = item.Id,
                                ["turns"] = turns,
                                ["processingMs"] = Round(durationMs, 1),
                                ["peakMemoryMb"] = (int)Math.Round(result.PeakWorkingSetMb),
                            };
                            gpuPeak = Math.Max(gpuPeak, result.PeakGpuDedicatedMb);
                            torchAllocatedPeak = Math.Max(torchAllocatedPeak, response["gpuAllocatedPeakMb"]?.GetValue<double>() ?? 0);
                            torchReservedPeak = Math.Max(torchReservedPeak, response["gpuReservedPeakMb"]?.GetValue<double>() ?? 0);
                            var speakers = turns.Select(t => t![2]!.ToString()).Distinct().Count();
                            Console.WriteLine(Invariant($"  {item.Id}: {speakers}/{item.Speakers} speakers, {item.Seconds / (durationMs / 1000):F0}x, {result.PeakWorkingSetMb:F0} MB RAM, {result.PeakGpuDedicatedMb:F0} MB GPU"));
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
                    {
                        entry = Failed(item, ex is OperationCanceledException ? $"timeout after {timeout.TotalSeconds:F0} s" : ex.Message);
                        // A dead or stuck worker is replaced; its reload is not timed.
                        worker.Dispose();
                        worker = WorkerProcess.Start(engine, paths);
                        var reload = await worker.RequestAsync(new JsonObject { ["op"] = "load", ["config"] = engine.Config(paths) }, TimeSpan.FromMinutes(30));
                        if (reload.Response["error"] is { } reloadError)
                        {
                            throw new InvalidOperationException($"{engine.Id} failed to reload: {reloadError}");
                        }
                    }

                    if (entry["error"] is not null)
                    {
                        Console.WriteLine($"  {item.Id}: ERROR {entry["error"]}");
                    }

                    results[item.Id] = entry;
                    if (engine.UsesGpu)
                    {
                        settings["peakGpuDedicatedMb"] = (int)Math.Round(Math.Max(gpuPeak, ToDouble(earlier?["settings"]?["peakGpuDedicatedMb"])));
                    }

                    if (torchAllocatedPeak > 0)
                    {
                        settings["torchPeakAllocatedMb"] = (int)Math.Round(Math.Max(torchAllocatedPeak, ToDouble(earlier?["settings"]?["torchPeakAllocatedMb"])));
                        settings["torchPeakReservedMb"] = (int)Math.Round(Math.Max(torchReservedPeak, ToDouble(earlier?["settings"]?["torchPeakReservedMb"])));
                    }

                    var file = new JsonObject
                    {
                        ["schema"] = 1,
                        ["setId"] = set.Id,
                        ["platform"] = "windows",
                        ["engineId"] = engine.Id,
                        ["engineName"] = engine.Name,
                        ["runtime"] = runtime,
                        ["model"] = engine.Model,
                        ["shipped"] = false,
                        ["settings"] = ToJson(settings),
                        ["machine"] = machine.DeepClone(),
                        ["measuredAt"] = earlier?["measuredAt"]?.GetValue<string>() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                        ["prepareMs"] = earlier?["prepareMs"]?.GetValue<double>() ?? Round(prepareMs, 1),
                        // Set order, so a resumed file reads the same as one from a single run.
                        ["items"] = new JsonArray(set.Items.Where(i => results.ContainsKey(i.Id)).Select(i => (JsonNode)results[i.Id].DeepClone()).ToArray()),
                    };
                    // Replaced in one step, so an interrupted write never leaves a broken checkpoint behind.
                    var partial = path + ".partial";
                    await File.WriteAllTextAsync(partial, file.ToJsonString() + "\n");
                    File.Move(partial, path, overwrite: true);
                }

                written.Add(path);
                Console.WriteLine($"{engine.Id}: wrote {path}");
            }
            finally
            {
                worker.Dispose();
            }
        }

        return written;
    }

    private static JsonObject Failed(SetItem item, string error) =>
        new() { ["id"] = item.Id, ["turns"] = new JsonArray(), ["processingMs"] = 0, ["error"] = error };

    private static void FlattenInto(Dictionary<string, object?> settings, JsonObject info, string[] skip, string prefix = "")
    {
        foreach (var (key, value) in info)
        {
            if (skip.Contains(key))
            {
                continue;
            }

            var name = prefix.Length == 0 ? key : prefix + char.ToUpperInvariant(key[0]) + key[1..];
            switch (value)
            {
                // The workers' own settings and streaming profile go in as they are.
                case JsonObject child when prefix.Length == 0 && key is "settings" or "profile":
                    FlattenInto(settings, child, [], "");
                    break;
                case JsonObject child:
                    FlattenInto(settings, child, [], name);
                    break;
                case JsonValue v when v.TryGetValue<bool>(out var b):
                    settings[name] = b;
                    break;
                case JsonValue v when v.TryGetValue<double>(out var d):
                    settings[name] = d;
                    break;
                case null:
                    settings[name] = null;
                    break;
                default:
                    settings[name] = value.ToString();
                    break;
            }
        }
    }

    private static JsonObject ToJson(Dictionary<string, object?> settings)
    {
        var json = new JsonObject();
        foreach (var (key, value) in settings)
        {
            json[key] = value switch
            {
                null => null,
                bool b => b,
                int i => i,
                double d => d,
                float f => (double)f,
                _ => value.ToString(),
            };
        }

        return json;
    }

    private static readonly string[] MeasuredSettings =
        ["modelLoadMs", "peakGpuDedicatedMb", "torchPeakAllocatedMb", "torchPeakReservedMb"];

    /// <summary>The settings that describe how a run was configured, without the values it measured.</summary>
    private static JsonObject Configuration(JsonObject? settings)
    {
        var configuration = new JsonObject();
        foreach (var (key, value) in settings ?? [])
        {
            if (!MeasuredSettings.Contains(key))
            {
                configuration[key] = value?.DeepClone();
            }
        }

        return configuration;
    }

    private static double ToDouble(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    public static double Round(double value, int digits = 3) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    public static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
