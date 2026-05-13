using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal interface ISherpaCudaRuntimeInstaller
{
    bool IsInstalled { get; }
    string? RuntimeDirectory { get; }
    Task EnsureInstalledAsync(CancellationToken cancellationToken);
}

internal sealed class SherpaCudaRuntimeInstaller : ISherpaCudaRuntimeInstaller
{
    internal const string RuntimeVersion = "v1.13.0";
    internal const string AssetFileName = "sherpa-onnx-v1.13.0-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2";
    internal const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.0/" + AssetFileName;

    private static readonly string[] CoreRuntimeFiles =
    [
        "sherpa-onnx-c-api.dll",
        "onnxruntime.dll",
        "onnxruntime_providers_cuda.dll"
    ];

    private static readonly CudaDependencyPackage[] CudaDependencyPackages =
    [
        new(
            "nvidia-cuda-runtime-cu12",
            "12.9.79",
            ["cudart64_12.dll"]),
        new(
            "nvidia-cublas-cu12",
            "12.9.2.10",
            ["cublas64_12.dll", "cublasLt64_12.dll"]),
        new(
            "nvidia-cufft-cu12",
            "11.4.1.4",
            ["cufft64_11.dll"]),
        new(
            "nvidia-cudnn-cu12",
            "9.22.0.52",
            [
                "cudnn64_9.dll",
                "cudnn_adv64_9.dll",
                "cudnn_cnn64_9.dll",
                "cudnn_engines_precompiled64_9.dll",
                "cudnn_engines_runtime_compiled64_9.dll",
                "cudnn_graph64_9.dll",
                "cudnn_heuristic64_9.dll",
                "cudnn_ops64_9.dll"
            ])
    ];

    private static readonly string[] RequiredFiles =
        CoreRuntimeFiles.Concat(CudaDependencyPackages.SelectMany(package => package.RequiredDlls)).ToArray();

    private readonly string _runtimeRoot;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SherpaCudaRuntimeInstaller(string pluginDataDirectory, HttpClient httpClient)
    {
        _runtimeRoot = Path.Combine(pluginDataDirectory, "Runtimes", "sherpa-onnx-cuda", RuntimeVersion);
        _httpClient = httpClient;
    }

    public string RuntimeDirectory => Path.Combine(_runtimeRoot, "native");

    public bool IsInstalled => RequiredFiles.All(file => File.Exists(Path.Combine(RuntimeDirectory, file)));

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled)
                return;

            Directory.CreateDirectory(_runtimeRoot);
            Directory.CreateDirectory(RuntimeDirectory);

            if (!HasRequiredFiles(CoreRuntimeFiles))
                await InstallSherpaRuntimeAsync(cancellationToken);

            await InstallCudaProviderDependenciesAsync(cancellationToken);
            ValidateInstalledRuntime();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InstallSherpaRuntimeAsync(CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(_runtimeRoot, $"extract-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(_runtimeRoot, $"{AssetFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await DownloadArchiveAsync(archivePath, cancellationToken);
            Directory.CreateDirectory(tempRoot);
            ExtractArchive(archivePath, tempRoot);

            var nativeSource = FindNativeRuntimeDirectory(tempRoot)
                ?? throw new InvalidOperationException(
                    "The downloaded sherpa-onnx CUDA runtime did not contain sherpa-onnx-c-api.dll.");

            foreach (var file in Directory.EnumerateFiles(nativeSource))
            {
                var destination = Path.Combine(RuntimeDirectory, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: true);
            }
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task InstallCudaProviderDependenciesAsync(CancellationToken cancellationToken)
    {
        foreach (var package in CudaDependencyPackages)
        {
            if (HasRequiredFiles(package.RequiredDlls))
                continue;

            var wheelUrl = await ResolveWheelUrlAsync(package, cancellationToken);
            var wheelPath = Path.Combine(
                _runtimeRoot,
                $"{package.PackageName}-{package.Version}.{Guid.NewGuid():N}.whl.tmp");

            try
            {
                await DownloadFileAsync(wheelUrl, wheelPath, cancellationToken);
                ExtractDllsFromWheel(wheelPath, RuntimeDirectory);
            }
            finally
            {
                TryDeleteFile(wheelPath);
            }

            var missing = package.RequiredDlls
                .Where(file => !File.Exists(Path.Combine(RuntimeDirectory, file)))
                .ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The CUDA dependency package {package.PackageName} {package.Version} is incomplete. Missing: "
                    + string.Join(", ", missing));
            }
        }
    }

    private async Task DownloadArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        await DownloadFileAsync(DownloadUrl, archivePath, cancellationToken);
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task<string> ResolveWheelUrlAsync(
        CudaDependencyPackage package,
        CancellationToken cancellationToken)
    {
        var metadataUrl = $"https://pypi.org/pypi/{package.PackageName}/{package.Version}/json";
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (var file in document.RootElement.GetProperty("urls").EnumerateArray())
        {
            var filename = file.GetProperty("filename").GetString();
            if (filename is null || !filename.EndsWith("win_amd64.whl", StringComparison.OrdinalIgnoreCase))
                continue;

            if (file.TryGetProperty("packagetype", out var packageType)
                && !string.Equals(packageType.GetString(), "bdist_wheel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = file.GetProperty("url").GetString();
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        throw new InvalidOperationException(
            $"Could not find a Windows x64 wheel for {package.PackageName} {package.Version}.");
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, null);
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            entry.WriteToDirectory(destinationDirectory, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }
    }

    private static void ExtractDllsFromWheel(string wheelPath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(wheelPath);
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var destination = Path.Combine(destinationDirectory, entry.Name);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static string? FindNativeRuntimeDirectory(string rootDirectory) =>
        Directory
            .EnumerateFiles(rootDirectory, "sherpa-onnx-c-api.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

    private bool HasRequiredFiles(IEnumerable<string> files) =>
        files.All(file => File.Exists(Path.Combine(RuntimeDirectory, file)));

    private void ValidateInstalledRuntime()
    {
        var missing = RequiredFiles
            .Where(file => !File.Exists(Path.Combine(RuntimeDirectory, file)))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The sherpa-onnx CUDA runtime is incomplete. Missing: " + string.Join(", ", missing));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record CudaDependencyPackage(
        string PackageName,
        string Version,
        IReadOnlyList<string> RequiredDlls);
}
