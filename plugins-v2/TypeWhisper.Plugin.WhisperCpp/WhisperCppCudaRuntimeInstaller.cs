using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

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

internal sealed class WhisperCppCudaRuntimeInstaller : IWhisperCppCudaRuntimeInstaller, IDisposable
{
    private const string CudaRuntimeIdentifier = "win-x64";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly WhisperCppCudaRuntimePackage DefaultPackage = new(
        "cuda-13.3.0-cublas-13.5.1.27",
        "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.5.1.27-archive.zip",
        "c946e1c825e05895747a95ed4fee18030b08052c09783b9b7b19818fd2e31f58",
        ["cublas64_13.dll", "cublasLt64_13.dll"]);

    private readonly HttpClient _httpClient;
    private readonly WhisperCppCudaRuntimePackage _package;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (long Length, DateTime Written, string Hash)> _verifiedFiles = new();
    private readonly object _verificationLock = new();
    private string ReceiptPath => Path.Join(RuntimeDirectory, "installed.json");
    private sealed record RuntimeReceipt(string Version, string ArchiveHash, Dictionary<string, string> Files);

    /// <summary>
    /// Performs whisper cpp cuda runtime installer.
    /// </summary>
    public WhisperCppCudaRuntimeInstaller(string assetDirectory, HttpClient httpClient)
        : this(assetDirectory, httpClient, DefaultPackage)
    {
    }

    internal WhisperCppCudaRuntimeInstaller(
        string assetDirectory,
        HttpClient httpClient,
        WhisperCppCudaRuntimePackage package)
    {
        var assetRoot = Path.GetFullPath(assetDirectory);
        RuntimeDirectory = Path.Join(assetRoot, "runtimes", "cuda", CudaRuntimeIdentifier);
        _httpClient = httpClient;
        _package = package;
    }

    /// <summary>
    /// Gets the runtime directory.
    /// </summary>
    public string RuntimeDirectory { get; }

    /// <summary>
    /// Returns whether installed.
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            lock (_verificationLock)
            {
                try
                {
                    if (!File.Exists(ReceiptPath)) return false;
                    var receipt = JsonSerializer.Deserialize<RuntimeReceipt>(File.ReadAllText(ReceiptPath));
                    if (receipt?.Version != _package.RuntimeVersion || receipt.ArchiveHash != _package.Sha256 || receipt.Files is null)
                        return false;
                    foreach (var name in _package.RequiredDlls)
                    {
                        var file = new FileInfo(GetRuntimeFilePath(name));
                        if (!file.Exists || file.Length == 0 || !receipt.Files.TryGetValue(name, out var expected)) return false;
                        if (!_verifiedFiles.TryGetValue(name, out var cached) || cached.Length != file.Length || cached.Written != file.LastWriteTimeUtc)
                        {
                            using var input = file.OpenRead();
                            cached = (file.Length, file.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(input)));
                            _verifiedFiles[name] = cached;
                        }
                        if (!string.Equals(cached.Hash, expected, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { return false; }
            }
        }
    }

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

            Directory.CreateDirectory(RuntimeDirectory);

            var archivePath = Path.Join(
                RuntimeDirectory,
                $"nvidia-cublas-{_package.RuntimeVersion}.{Guid.NewGuid():N}.zip.tmp");

            try
            {
                await DownloadArchiveAsync(archivePath, cancellationToken);
                await ValidateArchiveHashAsync(archivePath, cancellationToken);
                ExtractRequiredDlls(archivePath);
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
        using var timeoutCts = new CancellationTokenSource(DownloadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        var downloadToken = linkedCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, _package.DownloadUrl);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                downloadToken);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(downloadToken);
            await using var output = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await input.CopyToAsync(output, downloadToken);
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested
            && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("The NVIDIA CUDA runtime download timed out.", ex);
        }
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
        var staged = new List<(string Name, string Path)>();
        var hashes = new Dictionary<string, string>();
        var receiptTemporary = ReceiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            foreach (var name in _package.RequiredDlls)
            {
                var entry = archive.Entries.SingleOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length == 0)
                    throw new InvalidOperationException("The NVIDIA CUDA runtime download was incomplete. Missing: " + name);
                var temporary = GetRuntimeFilePath(name) + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((name, temporary));
                entry.ExtractToFile(temporary);
                using var input = File.OpenRead(temporary);
                hashes[name] = Convert.ToHexString(SHA256.HashData(input));
            }
            File.WriteAllText(receiptTemporary, JsonSerializer.Serialize(new RuntimeReceipt(_package.RuntimeVersion, _package.Sha256, hashes)));
            lock (_verificationLock)
            {
                // Invalidate the receipt before replacing files; interruption must never
                // make a partial installation appear complete on the next startup.
                File.Delete(ReceiptPath);
                _verifiedFiles.Clear();
                foreach (var file in staged) File.Move(file.Path, GetRuntimeFilePath(file.Name), overwrite: true);
                File.Move(receiptTemporary, ReceiptPath, overwrite: true);
            }
        }
        finally
        {
            foreach (var file in staged) TryDeleteFile(file.Path);
            TryDeleteFile(receiptTemporary);
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

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => _gate.Dispose();
}
