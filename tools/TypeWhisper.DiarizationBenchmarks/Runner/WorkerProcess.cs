using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

namespace TypeWhisper.DiarizationBenchmarks;

/// <summary>
/// One worker process speaking the JSON-lines protocol. While a request runs, its working set and its
/// dedicated GPU memory (Windows "GPU Process Memory" counters, which cover CUDA and DirectML alike) are
/// sampled, so each file gets its own peak.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly string _label;

    // The process whose memory is sampled: the worker itself, or the interpreter it reports in its load
    // response (a venv's python.exe only launches the real interpreter as a child process).
    private Process _measured;

    private WorkerProcess(Process process, string label)
    {
        _process = process;
        _measured = process;
        _label = label;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && Verbose)
            {
                Console.Error.WriteLine($"  [{_label}] {e.Data}");
            }
        };
        _process.BeginErrorReadLine();
    }

    public static bool Verbose { get; set; }

    public int Id => _process.Id;

    public static WorkerProcess Start(Engine engine, Paths paths)
    {
        var info = engine.Worker switch
        {
            WorkerKind.Nemo => PythonWorker(paths, "nemo", "nemo_worker.py"),
            WorkerKind.Pyannote => PythonWorker(paths, "pyannote", "pyannote_worker.py"),
            _ => new ProcessStartInfo(paths.WorkerExe(engine.Worker)),
        };
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        // No telemetry or Hub requests from the Python workers; every model is local.
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["PYANNOTE_METRICS_ENABLED"] = "0";
        info.Environment["WANDB_DISABLED"] = "true";
        var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {info.FileName}");
        return new WorkerProcess(process, engine.Id);
    }

    private static ProcessStartInfo PythonWorker(Paths paths, string project, string script)
    {
        var python = paths.Python(project);
        if (!File.Exists(python))
        {
            throw new FileNotFoundException($"{python} is missing; run the setup command first");
        }

        var info = new ProcessStartInfo(python) { WorkingDirectory = paths.PythonProject(project) };
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add(Path.Combine(paths.PythonProject(project), script));
        return info;
    }

    public sealed record Result(JsonObject Response, double WallMs, double PeakWorkingSetMb, double PeakGpuDedicatedMb);

    public async Task<Result> RequestAsync(JsonObject request, TimeSpan timeout)
    {
        using var sampling = new CancellationTokenSource();
        var peaks = new Peaks();
        var sampler = Task.Run(() => SampleAsync(peaks, sampling.Token));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _process.StandardInput.WriteLineAsync(request.ToJsonString());
            await _process.StandardInput.FlushAsync();
            using var timeoutSource = new CancellationTokenSource(timeout);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeoutSource.Token)
                    ?? throw new InvalidOperationException($"{_label} exited with code {(_process.HasExited ? _process.ExitCode : -1)}");
                // Native libraries may print to stdout; protocol lines are JSON objects.
                if (!line.StartsWith('{'))
                {
                    if (Verbose)
                    {
                        Console.Error.WriteLine($"  [{_label} stdout] {line}");
                    }

                    continue;
                }

                var wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await sampling.CancelAsync();
                await sampler;
                var response = JsonNode.Parse(line)!.AsObject();
                if (response["pid"] is JsonValue pidValue && pidValue.TryGetValue<int>(out var pid) && pid != _measured.Id)
                {
                    _measured = Process.GetProcessById(pid);
                }

                return new Result(response, wallMs, peaks.WorkingSetMb, peaks.GpuMb);
            }
        }
        finally
        {
            await sampling.CancelAsync();
        }
    }

    private sealed class Peaks
    {
        public double WorkingSetMb;
        public double GpuMb;
    }

    private async Task SampleAsync(Peaks peaks, CancellationToken token)
    {
        var tick = 0;
        while (true)
        {
            try
            {
                _measured.Refresh();
                if (_measured.HasExited)
                {
                    return;
                }

                peaks.WorkingSetMb = Math.Max(peaks.WorkingSetMb, _measured.WorkingSet64 / 1048576.0);
                if (tick++ % 5 == 0)
                {
                    peaks.GpuMb = Math.Max(peaks.GpuMb, GpuMemory.DedicatedMb(_measured.Id));
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                await Task.Delay(50, token);
            }
            catch (OperationCanceledException)
            {
                // One last GPU sample, so short files still get one.
                peaks.GpuMb = Math.Max(peaks.GpuMb, GpuMemory.DedicatedMb(_measured.Id));
                return;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("{\"op\":\"quit\"}");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(15000))
                {
                    _process.Kill(entireProcessTree: true);
                    // Kill returns before the process is gone; a replacement must not share the GPU with it.
                    _process.WaitForExit();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Already gone.
        }

        if (_measured != _process)
        {
            // The interpreter behind a venv launcher.
            try
            {
                if (!_measured.HasExited)
                {
                    _measured.Kill(entireProcessTree: true);
                    _measured.WaitForExit();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            _measured.Dispose();
        }

        _process.Dispose();
    }
}

/// <summary>Dedicated GPU memory of one process, from the "GPU Process Memory" performance counters.</summary>
[SupportedOSPlatform("windows")]
internal static class GpuMemory
{
    private static readonly PerformanceCounterCategory? Category = TryCategory();

    private static PerformanceCounterCategory? TryCategory()
    {
        try
        {
            return PerformanceCounterCategory.Exists("GPU Process Memory") ? new PerformanceCounterCategory("GPU Process Memory") : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static double DedicatedMb(int pid)
    {
        if (Category is null)
        {
            return 0;
        }

        try
        {
            var prefix = $"pid_{pid}_";
            double bytes = 0;
            foreach (var instance in Category.GetInstanceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
            {
                using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                bytes += counter.RawValue;
            }

            return bytes / 1048576.0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
