using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
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
    private const string SherpaNativeLibraryFileName = "sherpa-onnx-c-api.dll";
    private const string OnnxRuntimeFileName = "onnxruntime.dll";
    private const string SherpaOnnxRuntimeDependencyFileName = "sherpaort.dll";
    private const string OnnxRuntimeCudaProviderFileName = "onnxruntime_providers_cuda.dll";

    private static readonly string[] DownloadedRuntimeFiles =
    [
        SherpaNativeLibraryFileName,
        OnnxRuntimeFileName,
        OnnxRuntimeCudaProviderFileName
    ];

    private static readonly string[] CoreRuntimeFiles =
    [
        .. DownloadedRuntimeFiles,
        SherpaOnnxRuntimeDependencyFileName
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

    /// <summary>
    /// Performs sherpa cuda runtime installer.
    /// </summary>
    public SherpaCudaRuntimeInstaller(string pluginDataDirectory, HttpClient httpClient)
    {
        var pluginDataRoot = Path.GetFullPath(pluginDataDirectory);
        _runtimeRoot = Path.Join(pluginDataRoot, "Runtimes", "sherpa-onnx-cuda", RuntimeVersion);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Performs runtime directory.
    /// </summary>
    public string RuntimeDirectory => Path.Join(_runtimeRoot, "native");

    /// <summary>
    /// Returns whether installed.
    /// </summary>
    public bool IsInstalled =>
        RequiredFiles.All(file => File.Exists(GetRuntimeFilePath(file)))
        && IsRuntimeImportPatched(GetRuntimeFilePath(SherpaNativeLibraryFileName));

    /// <summary>
    /// Ensures installed asynchronously..
    /// </summary>
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

            if (!HasRequiredFiles(DownloadedRuntimeFiles))
                await InstallSherpaRuntimeAsync(cancellationToken);

            EnsureSherpaRuntimeImportAlias(RuntimeDirectory);
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
        var tempRoot = Path.Join(_runtimeRoot, $"extract-{Guid.NewGuid():N}");
        var safeAssetFileName = Path.GetFileName(AssetFileName);
        var archivePath = Path.Join(_runtimeRoot, $"{safeAssetFileName}.{Guid.NewGuid():N}.tmp");

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
                var destination = Path.Join(RuntimeDirectory, Path.GetFileName(file));
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
        foreach (var package in CudaDependencyPackages.Where(package => !HasRequiredFiles(package.RequiredDlls)))
        {
            var wheelUrl = await ResolveWheelUrlAsync(package, cancellationToken);
            var wheelPath = Path.Join(
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
                .Where(file => !File.Exists(GetRuntimeFilePath(file)))
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
            var destination = Path.Join(destinationDirectory, Path.GetFileName(entry.Name));
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static string? FindNativeRuntimeDirectory(string rootDirectory) =>
        Directory
            .EnumerateFiles(rootDirectory, SherpaNativeLibraryFileName, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

    internal static void EnsureSherpaRuntimeImportAlias(string runtimeDirectory)
    {
        var onnxRuntimePath = Path.Join(runtimeDirectory, OnnxRuntimeFileName);
        var sherpaRuntimePath = Path.Join(runtimeDirectory, SherpaOnnxRuntimeDependencyFileName);
        if (!File.Exists(onnxRuntimePath))
            throw new InvalidOperationException($"The sherpa-onnx CUDA runtime is missing {OnnxRuntimeFileName}.");

        File.Copy(onnxRuntimePath, sherpaRuntimePath, overwrite: true);

        PatchRuntimeImport(Path.Join(runtimeDirectory, SherpaNativeLibraryFileName), requireImport: true);
        PatchRuntimeImport(Path.Join(runtimeDirectory, OnnxRuntimeCudaProviderFileName), requireImport: false);
    }

    private static void PatchRuntimeImport(string libraryPath, bool requireImport)
    {
        if (!File.Exists(libraryPath))
        {
            if (requireImport)
                throw new InvalidOperationException($"The sherpa-onnx CUDA runtime is missing {Path.GetFileName(libraryPath)}.");

            return;
        }

        var original = Encoding.ASCII.GetBytes(OnnxRuntimeFileName + '\0');
        var patchedBytes = Encoding.ASCII.GetBytes(SherpaOnnxRuntimeDependencyFileName + '\0');
        var patched = new byte[original.Length];
        Array.Copy(patchedBytes, patched, patchedBytes.Length);

        var bytes = File.ReadAllBytes(libraryPath);
        var originalOffsets = FindNeedleOffsets(bytes, original).ToList();
        var patchedOffsets = FindNeedleOffsets(bytes, patched).ToList();

        if (originalOffsets.Count == 0 && patchedOffsets.Count > 0)
            return;

        if (originalOffsets.Count == 0)
        {
            if (requireImport)
            {
                throw new InvalidOperationException(
                    $"Expected {Path.GetFileName(libraryPath)} to import {OnnxRuntimeFileName}.");
            }

            return;
        }

        foreach (var offset in originalOffsets)
            Array.Copy(patched, 0, bytes, offset, patched.Length);

        File.WriteAllBytes(libraryPath, bytes);
    }

    private static bool IsRuntimeImportPatched(string libraryPath)
    {
        if (!File.Exists(libraryPath))
            return false;

        var bytes = File.ReadAllBytes(libraryPath);
        var original = Encoding.ASCII.GetBytes(OnnxRuntimeFileName + '\0');
        var patchedBytes = Encoding.ASCII.GetBytes(SherpaOnnxRuntimeDependencyFileName + '\0');
        var patched = new byte[original.Length];
        Array.Copy(patchedBytes, patched, patchedBytes.Length);

        return !FindNeedleOffsets(bytes, original).Any()
               && FindNeedleOffsets(bytes, patched).Any();
    }

    private static IEnumerable<int> FindNeedleOffsets(byte[] bytes, byte[] needle)
    {
        for (var offset = 0; offset <= bytes.Length - needle.Length; offset++)
        {
            var matches = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (bytes[offset + index] == needle[index])
                    continue;

                matches = false;
                break;
            }

            if (matches)
                yield return offset;
        }
    }

    private bool HasRequiredFiles(IEnumerable<string> files) =>
        files.All(file => File.Exists(GetRuntimeFilePath(file)));

    private void ValidateInstalledRuntime()
    {
        var missing = RequiredFiles
            .Where(file => !File.Exists(GetRuntimeFilePath(file)))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The sherpa-onnx CUDA runtime is incomplete. Missing: " + string.Join(", ", missing));
        }
    }

    private string GetRuntimeFilePath(string fileName) =>
        Path.Join(RuntimeDirectory, Path.GetFileName(fileName));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Failed to delete temporary file '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to delete temporary file '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Failed to delete temporary directory '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to delete temporary directory '{path}': {ex.Message}");
        }
    }

    private sealed record CudaDependencyPackage(
        string PackageName,
        string Version,
        IReadOnlyList<string> RequiredDlls);
}
