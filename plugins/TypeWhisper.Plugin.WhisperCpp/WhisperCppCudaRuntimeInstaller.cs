using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace TypeWhisper.Plugin.WhisperCpp;

internal interface IWhisperCppCudaRuntimeInstaller
{
    bool IsInstalled { get; }
    string RuntimeDirectory { get; }
    Task EnsureInstalledAsync(CancellationToken cancellationToken);
}

internal sealed record WhisperCppCudaRuntimePackage(
    string RuntimeVersion,
    string DownloadUrl,
    string Sha256,
    IReadOnlyList<string> RequiredDlls);

internal sealed class WhisperCppCudaRuntimeInstaller : IWhisperCppCudaRuntimeInstaller
{
    private const string CudaRuntimeIdentifier = "win-x64";

    private static readonly WhisperCppCudaRuntimePackage DefaultPackage = new(
        "cuda-13.3.0-cublas-13.5.1.27",
        "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.5.1.27-archive.zip",
        "c946e1c825e05895747a95ed4fee18030b08052c09783b9b7b19818fd2e31f58",
        ["cublas64_13.dll", "cublasLt64_13.dll"]);

    private readonly HttpClient _httpClient;
    private readonly WhisperCppCudaRuntimePackage _package;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperCppCudaRuntimeInstaller(string pluginDirectory, HttpClient httpClient)
        : this(pluginDirectory, httpClient, DefaultPackage)
    {
    }

    internal WhisperCppCudaRuntimeInstaller(
        string pluginDirectory,
        HttpClient httpClient,
        WhisperCppCudaRuntimePackage package)
    {
        var pluginRoot = Path.GetFullPath(pluginDirectory);
        RuntimeDirectory = Path.Join(pluginRoot, "runtimes", "cuda", CudaRuntimeIdentifier);
        _httpClient = httpClient;
        _package = package;
    }

    public string RuntimeDirectory { get; }

    public bool IsInstalled => _package.RequiredDlls.All(file =>
        File.Exists(GetRuntimeFilePath(file)));

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled)
                return;

            Directory.CreateDirectory(RuntimeDirectory);

            var archivePath = Path.Join(
                RuntimeDirectory,
                $"nvidia-cublas-{_package.RuntimeVersion}.{Guid.NewGuid():N}.zip.tmp");

            try
            {
                await DownloadArchiveAsync(archivePath, cancellationToken);
                await ValidateArchiveHashAsync(archivePath, cancellationToken);
                ExtractRequiredDlls(archivePath);
                ValidateInstalledRuntime();
            }
            finally
            {
                TryDeleteFile(archivePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _package.DownloadUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task ValidateArchiveHashAsync(string archivePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, _package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The downloaded NVIDIA CUDA runtime did not match the expected checksum.");
        }
    }

    private void ExtractRequiredDlls(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var remaining = new HashSet<string>(_package.RequiredDlls, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries.Where(entry =>
                     remaining.Contains(entry.Name)))
        {
            var destination = GetRuntimeFilePath(entry.Name);
            entry.ExtractToFile(destination, overwrite: true);
            remaining.Remove(entry.Name);
        }
    }

    private void ValidateInstalledRuntime()
    {
        var missing = _package.RequiredDlls
            .Where(file => !File.Exists(GetRuntimeFilePath(file)))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The NVIDIA CUDA runtime download was incomplete. Missing: " + string.Join(", ", missing));
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
            Debug.WriteLine($"Failed to delete temporary CUDA runtime file '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to delete temporary CUDA runtime file '{path}': {ex.Message}");
        }
    }
}
