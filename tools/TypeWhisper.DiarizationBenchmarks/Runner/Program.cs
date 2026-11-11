using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TypeWhisper.DiarizationBenchmarks;

const string Usage = """
    Diarization benchmark for TypeWhisper on Windows. Writes hypothesis files in the format of
    TypeWhisper/typewhisper-benchmark bench/diarization; scoring happens there.

      setup     download audio and models, create the Python environments, build the workers
      run       run engines over the set (default: the six main engines)
      compare   diarize a few files with NeMo and the ONNX port and compare their frame probabilities
      machine   print the machine block of the hypothesis files
      engines   list the engine ids

    Options:
      --data <dir>        data directory (audio, models, venvs, outputs); default
                          %TYPEWHISPER_DIARIZATION_DATA%
      --engines a,b       engine ids (run)
      --items p,q         item id prefixes, for example synthetic/ or ami/ES2004a/
      --pairs fast32|offline|all   profiles to compare (compare; default fast32)
      --resume            keep the files an earlier run of the engine finished
      --threads N         CPU threads for the CPU engines (default: physical cores)
      --skip-checksums    do not verify the audio against the manifest
      --allow-busy-gpu    run even when the GPU is busy with something else
      --verbose           show the workers' stderr
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

var command = args[0];
var options = ParseOptions(args.Skip(1).ToArray());
var data = options.GetValueOrDefault("data") ?? Environment.GetEnvironmentVariable("TYPEWHISPER_DIARIZATION_DATA");
WorkerProcess.Verbose = options.ContainsKey("verbose");
if (options.TryGetValue("threads", out var threadsText) && threadsText is not null)
{
    Engines.CpuThreads = int.Parse(threadsText, CultureInfo.InvariantCulture);
}

switch (command)
{
    case "engines":
        foreach (var engine in Engines.All)
        {
            Console.WriteLine($"{engine.Id,-30} {engine.Name}{(Engines.Default.Contains(engine.Id) ? "" : "  (extra)")}");
        }

        return 0;
    case "machine":
        Console.WriteLine(Machine.Describe(Machine.Gpu()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
}

if (string.IsNullOrEmpty(data))
{
    Console.Error.WriteLine("Pass --data <dir> or set TYPEWHISPER_DIARIZATION_DATA.");
    return 2;
}

var paths = new Paths(Path.GetFullPath(data), Paths.FindToolRoot());
if (command == "setup")
{
    await Setup.RunAsync(paths);
    return 0;
}

var set = BenchmarkSet.Load(paths.Manifest);
var prefixes = options.GetValueOrDefault("items")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
var items = prefixes is null ? set.Items : set.Items.Where(i => prefixes.Any(p => i.Id.StartsWith(p, StringComparison.Ordinal))).ToList();
if (items.Count == 0)
{
    Console.Error.WriteLine("No set item matches --items.");
    return 2;
}

if (!options.ContainsKey("skip-checksums"))
{
    Console.WriteLine($"Checking {items.Count} audio files");
    var problems = new BenchmarkSet(set.Id, items).Problems(paths.Audio);
    if (problems.Count > 0)
    {
        Console.Error.WriteLine("Audio does not match the set (run setup):\n  " + string.Join("\n  ", problems));
        return 1;
    }
}

if (!options.ContainsKey("allow-busy-gpu"))
{
    // A game or another model keeps the GPU busy; desktop activity comes in short bursts, so a busy
    // reading only counts when it lasts for a minute.
    var busy = GpuBusyPercent();
    for (var attempt = 1; busy > 10 && attempt < 6; attempt++)
    {
        Console.WriteLine($"GPU {busy:F0} % busy, checking again");
        Thread.Sleep(TimeSpan.FromSeconds(8));
        busy = GpuBusyPercent();
    }

    if (busy > 10)
    {
        Console.Error.WriteLine($"The GPU is {busy:F0} % busy with something else. Close games and other GPU work, or pass --allow-busy-gpu.");
        return 1;
    }
}

Setup.BuildWorkers(paths);
switch (command)
{
    case "run":
    {
        var ids = options.GetValueOrDefault("engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Engines.Default;
        var unknown = ids.Where(id => Engines.All.All(e => e.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Unknown engine {string.Join(", ", unknown)}; see the engines command.");
            return 2;
        }

        var engines = ids.Select(id => Engines.All.Single(e => e.Id == id)).ToList();
        var written = await Runner.RunAsync(paths, set, engines, items, options.ContainsKey("resume"));
        Console.WriteLine("Hypotheses:\n  " + string.Join("\n  ", written));
        return 0;
    }

    case "compare":
    {
        var which = options.GetValueOrDefault("pairs") ?? "fast32";
        var pairs = FrameCompare.Pairs.Where(p => which == "all" || p.Reference.EndsWith(which, StringComparison.Ordinal)).ToList();
        var report = await FrameCompare.RunAsync(paths, items, pairs);
        var output = Path.Combine(paths.Data, $"frame-agreement-{which}.json");
        await File.WriteAllTextAsync(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine("Report: " + output);
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command {command}.\n\n{Usage}");
        return 2;
}

static Dictionary<string, string?> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string?>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unexpected argument {args[i]}");
        }

        var name = args[i][2..];
        var takesValue = name is "data" or "engines" or "items" or "pairs" or "threads";
        options[name] = takesValue ? args[++i] : null;
    }

    return options;
}

// Median utilization over four seconds; anything but a near-idle GPU skews the timings. The median
// ignores single spikes, which read high while an idle GPU sits at its lowest clock.
static double GpuBusyPercent()
{
    try
    {
        var samples = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info)!;
            var line = process.StandardOutput.ReadLine();
            process.WaitForExit();
            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                samples.Add(value);
            }

            Thread.Sleep(400);
        }

        return samples.Count == 0 ? 0 : samples.Order().ElementAt(samples.Count / 2);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return 0;
    }
}
