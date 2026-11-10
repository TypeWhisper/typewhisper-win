using System.Diagnostics;
using System.Security.Cryptography;

namespace TypeWhisper.DiarizationBenchmarks;

/// <summary>
/// Prepares the data directory: the set's audio (through scripts/download_set.py), the pinned model
/// files, the Python environments (uv) and the C# workers. Everything already present is kept.
/// </summary>
internal static class Setup
{
    public static async Task RunAsync(Paths paths)
    {
        Directory.CreateDirectory(paths.Data);
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("typewhisper-diarization-benchmark");

        Console.WriteLine("Audio set");
        Run("python", ["-I", Path.Combine(paths.ToolRoot, "scripts", "download_set.py"), paths.Data]);

        foreach (var (url, relative, sha) in Pins.Files)
        {
            await DownloadAsync(http, url, Path.Combine(paths.Models, relative), sha);
        }

        if (!File.Exists(paths.SherpaSegmentation))
        {
            var archive = Path.Combine(paths.Models, "sherpa", "segmentation.tar.bz2");
            await DownloadAsync(http, Pins.SherpaSegmentationArchive, archive, null);
            // Windows' bsdtar reads bzip2.
            Run("tar", ["-xjf", archive, "-C", Path.GetDirectoryName(archive)!]);
            File.Delete(archive);
        }

        Verify(paths.SherpaSegmentation, Pins.SherpaSegmentationSha256);

        foreach (var project in new[] { "nemo", "pyannote" })
        {
            Console.WriteLine($"Python environment {project}");
            var env = new Dictionary<string, string> { ["UV_PROJECT_ENVIRONMENT"] = Path.Combine(paths.Venvs, project) };
            Run("python", ["-m", "uv", "sync", "--frozen", "--python", "3.12"], paths.PythonProject(project), env);
        }

        if (!File.Exists(Path.Combine(paths.PyannoteModel, "config.yaml")))
        {
            // Gated on Hugging Face: accept the conditions on the model page and set HF_TOKEN.
            Console.WriteLine("pyannote/speaker-diarization-community-1 (gated, needs HF_TOKEN)");
            Run(paths.Python("pyannote"), ["-I", "-c",
                "import os, sys; from huggingface_hub import snapshot_download; "
                + $"snapshot_download('pyannote/speaker-diarization-community-1', revision='{Pins.PyannoteRevision}', "
                + "local_dir=sys.argv[1], token=os.environ.get('HF_TOKEN'))", paths.PyannoteModel]);
        }

        BuildWorkers(paths);
        Console.WriteLine("Setup done: " + paths.Data);
    }

    public static void BuildWorkers(Paths paths)
    {
        foreach (var kind in new[] { WorkerKind.NemotronOnnxCpu, WorkerKind.NemotronOnnxDirectML, WorkerKind.Sherpa })
        {
            Run("dotnet", ["build", "-c", "Release", "--nologo", "-v", "q", paths.WorkerProject(kind)]);
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string target, string? sha)
    {
        if (File.Exists(target) && (sha is null || Hash(target) == sha))
        {
            return;
        }

        Console.WriteLine($"Downloading {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(part);
            await response.Content.CopyToAsync(file);
        }

        File.Move(part, target, overwrite: true);
        if (sha is not null)
        {
            Verify(target, sha);
        }
    }

    private static void Verify(string path, string sha)
    {
        var actual = Hash(path);
        if (actual != sha)
        {
            throw new InvalidDataException($"{path}: SHA-256 {actual}, expected {sha}");
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Run(string file, string[] args, string? workingDirectory = null, Dictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env ?? [])
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {file}");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed with exit code {process.ExitCode}");
        }
    }
}
