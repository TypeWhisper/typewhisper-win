using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TypeWhisper.Plugin.CohereTranscribe;

internal enum CrispAsrBackend
{
    Cpu,
    Cuda,
    Vulkan
}

internal sealed record RemoteArtifact(
    string FileName,
    string DownloadUrl,
    long SizeBytes,
    string Sha256);

internal sealed record RuntimePackage(
    CrispAsrBackend Backend,
    string Id,
    RemoteArtifact Archive);

internal sealed record CohereModelPaths(
    string ModelPath,
    string VadModelPath,
    string LanguageIdModelPath,
    string CacheDirectory);

internal readonly record struct ArtifactTransferProgress(long BytesTransferred, long TotalBytes);

internal interface ICohereLocalAssetManager
{
    long ModelTransferSize { get; }

    void SetHuggingFaceToken(string? token);

    bool IsModelInstalled();

    bool IsRuntimeInstalled(CrispAsrBackend backend);

    long GetRuntimeTransferSize(CrispAsrBackend backend);

    CohereModelPaths GetModelPaths();

    string GetRuntimeExecutablePath(CrispAsrBackend backend);

    Task EnsureModelAsync(IProgress<ArtifactTransferProgress>? progress, CancellationToken cancellationToken);

    Task EnsureRuntimeAsync(
        CrispAsrBackend backend,
        IProgress<ArtifactTransferProgress>? progress,
        CancellationToken cancellationToken);
}

internal sealed class CohereLocalAssetManager : ICohereLocalAssetManager, IDisposable
{
    private const int DefaultDownloadChunkSizeBytes = 16 * 1024 * 1024;
    private const int DefaultMaxDownloadAttempts = 5;

    private static readonly TimeSpan DefaultDownloadInactivityTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultDownloadRetryDelay = TimeSpan.FromSeconds(1);

    internal const string CrispAsrVersion = "0.8.24";
    internal const string CohereModelRevision = "2242638d5dfecc6f1dbe6c3a8713b97deb2e150f";
    internal const string VadModelRevision = "9ffd54a1e1ee413ddf265af9913beaf518d1639b";
    internal const string LanguageIdModelRevision = "95fb0613bf78c6e48305fccd9ce023ac15f0b5a6";

    internal static readonly RemoteArtifact CohereModel = new(
        "cohere-transcribe-q5_0.gguf",
        $"https://huggingface.co/cstr/cohere-transcribe-03-2026-GGUF/resolve/{CohereModelRevision}/cohere-transcribe-q5_0.gguf",
        1_738_722_944,
        "a09696c5cc2ed5052bf290c4f2beb35abc69c0d6986842042d92bebb22c9184e");

    internal static readonly RemoteArtifact VadModel = new(
        "ggml-silero-v6.2.0.bin",
        $"https://huggingface.co/ggml-org/whisper-vad/resolve/{VadModelRevision}/ggml-silero-v6.2.0.bin",
        885_098,
        "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987");

    internal static readonly RemoteArtifact LanguageIdModel = new(
        "ecapa-lid-107-f16.gguf",
        $"https://huggingface.co/cstr/ecapa-lid-107-GGUF/resolve/{LanguageIdModelRevision}/ecapa-lid-107-f16.gguf",
        42_838_944,
        "59db30ba67cec2f36304f794420779c181124332246f75fc66c349f184110340");

    internal static readonly RuntimePackage CpuRuntime = new(
        CrispAsrBackend.Cpu,
        "cpu",
        new RemoteArtifact(
            "crispasr-windows-x86_64-cpu.zip",
            $"https://github.com/CrispStrobe/CrispASR/releases/download/v{CrispAsrVersion}/crispasr-windows-x86_64-cpu.zip",
            7_635_428,
            "a05456c9adac276289060ae83bd77ed7b4d87ccfd447aed804e497d76e73f8f8"));

    internal static readonly RuntimePackage CudaRuntime = new(
        CrispAsrBackend.Cuda,
        "cuda",
        new RemoteArtifact(
            "crispasr-windows-x86_64-cuda.zip",
            $"https://github.com/CrispStrobe/CrispASR/releases/download/v{CrispAsrVersion}/crispasr-windows-x86_64-cuda.zip",
            729_126_487,
            "832af6218508ac52fc71ac5653c433786bdfae81f775e2c77bfefd05df6f255b"));

    internal static readonly RuntimePackage VulkanRuntime = new(
        CrispAsrBackend.Vulkan,
        "vulkan",
        new RemoteArtifact(
            "crispasr-windows-x86_64-vulkan.zip",
            $"https://github.com/CrispStrobe/CrispASR/releases/download/v{CrispAsrVersion}/crispasr-windows-x86_64-vulkan.zip",
            35_580_185,
            "70956638045042b49f61f9f04ee9ceae9fc8b450f6f56a10fc98c2088207628a"));

    private static readonly IReadOnlyList<RemoteArtifact> ModelArtifacts =
        [CohereModel, VadModel, LanguageIdModel];

    private readonly string _assetRoot;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly int _downloadChunkSizeBytes;
    private readonly TimeSpan _downloadInactivityTimeout;
    private readonly int _maxDownloadAttempts;
    private readonly TimeSpan _downloadRetryDelay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _huggingFaceToken;

    internal CohereLocalAssetManager(
        string assetRoot,
        HttpClient? httpClient = null,
        int downloadChunkSizeBytes = DefaultDownloadChunkSizeBytes,
        TimeSpan? downloadInactivityTimeout = null,
        int maxDownloadAttempts = DefaultMaxDownloadAttempts,
        TimeSpan? downloadRetryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(downloadChunkSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDownloadAttempts);

        var inactivityTimeout = downloadInactivityTimeout ?? DefaultDownloadInactivityTimeout;
        if (inactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(downloadInactivityTimeout));

        var retryDelay = downloadRetryDelay ?? DefaultDownloadRetryDelay;
        if (retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(downloadRetryDelay));

        _assetRoot = Path.GetFullPath(assetRoot);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _downloadChunkSizeBytes = downloadChunkSizeBytes;
        _downloadInactivityTimeout = inactivityTimeout;
        _maxDownloadAttempts = maxDownloadAttempts;
        _downloadRetryDelay = retryDelay;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TypeWhisper-CohereTranscribe/1.0");
    }

    public long ModelTransferSize => ModelArtifacts.Sum(static artifact => artifact.SizeBytes);

    public void SetHuggingFaceToken(string? token)
    {
        Volatile.Write(ref _huggingFaceToken, token);
    }

    public bool IsModelInstalled()
    {
        var paths = GetModelPaths();
        return IsArtifactReady(CohereModel, paths.ModelPath)
            && IsArtifactReady(VadModel, paths.VadModelPath)
            && IsArtifactReady(LanguageIdModel, paths.LanguageIdModelPath);
    }

    public bool IsRuntimeInstalled(CrispAsrBackend backend)
    {
        var package = GetRuntimePackage(backend);
        var runtimeDirectory = GetRuntimeDirectory(package);
        var markerPath = GetRuntimeMarkerPath(runtimeDirectory);

        return Directory.Exists(runtimeDirectory)
            && File.Exists(markerPath)
            && string.Equals(
                File.ReadAllText(markerPath).Trim(),
                package.Archive.Sha256,
                StringComparison.OrdinalIgnoreCase)
            && FindRuntimeExecutable(runtimeDirectory) is not null;
    }

    public long GetRuntimeTransferSize(CrispAsrBackend backend) =>
        GetRuntimePackage(backend).Archive.SizeBytes;

    public CohereModelPaths GetModelPaths()
    {
        var modelDirectory = Path.Join(_assetRoot, "Models", "cohere-transcribe-03-2026-q5_0");
        var auxiliaryDirectory = Path.Join(modelDirectory, "Auxiliary");

        return new CohereModelPaths(
            Path.Join(modelDirectory, CohereModel.FileName),
            Path.Join(auxiliaryDirectory, VadModel.FileName),
            Path.Join(auxiliaryDirectory, LanguageIdModel.FileName),
            Path.Join(_assetRoot, "Cache", "CrispASR"));
    }

    public string GetRuntimeExecutablePath(CrispAsrBackend backend)
    {
        var runtimeDirectory = GetRuntimeDirectory(GetRuntimePackage(backend));
        return FindRuntimeExecutable(runtimeDirectory)
            ?? throw new FileNotFoundException(
                $"CrispASR executable was not found in '{runtimeDirectory}'.");
    }

    public async Task EnsureModelAsync(
        IProgress<ArtifactTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var paths = GetModelPaths();
            var targets = new[]
            {
                (Artifact: CohereModel, Path: paths.ModelPath),
                (Artifact: VadModel, Path: paths.VadModelPath),
                (Artifact: LanguageIdModel, Path: paths.LanguageIdModelPath)
            };

            long completed = 0;
            foreach (var target in targets)
            {
                var completedBeforeArtifact = completed;
                var adapter = progress is null
                    ? null
                    : new Progress<ArtifactTransferProgress>(value =>
                        progress.Report(new ArtifactTransferProgress(
                            completedBeforeArtifact + value.BytesTransferred,
                            ModelTransferSize)));

                await EnsureArtifactAsync(
                    target.Artifact,
                    target.Path,
                    adapter,
                    cancellationToken);

                completed += target.Artifact.SizeBytes;
                progress?.Report(new ArtifactTransferProgress(completed, ModelTransferSize));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureRuntimeAsync(
        CrispAsrBackend backend,
        IProgress<ArtifactTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureSupportedPlatform();
        var package = GetRuntimePackage(backend);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRuntimeInstalled(backend))
            {
                progress?.Report(new ArtifactTransferProgress(
                    package.Archive.SizeBytes,
                    package.Archive.SizeBytes));
                return;
            }

            var runtimeDirectory = GetRuntimeDirectory(package);
            var runtimeParent = Path.GetDirectoryName(runtimeDirectory)
                ?? throw new InvalidOperationException("CrispASR runtime directory has no parent.");
            Directory.CreateDirectory(runtimeParent);

            var operationId = Guid.NewGuid().ToString("N");
            var archivePath = Path.Join(runtimeParent, $".{package.Archive.FileName}.download");
            var stagingDirectory = Path.Join(runtimeParent, $".{package.Id}.{operationId}.staging");
            EnsurePathWithinAssetRoot(runtimeDirectory);
            EnsurePathWithinAssetRoot(archivePath);
            EnsurePathWithinAssetRoot(stagingDirectory);

            try
            {
                await DownloadVerifiedAsync(
                    package.Archive,
                    archivePath,
                    progress,
                    cancellationToken);

                Directory.CreateDirectory(stagingDirectory);
                await ExtractArchiveAsync(archivePath, stagingDirectory, cancellationToken);

                if (FindRuntimeExecutable(stagingDirectory) is null)
                {
                    throw new InvalidDataException(
                        $"The verified CrispASR {package.Id} archive did not contain crispasr.exe.");
                }

                File.WriteAllText(
                    GetRuntimeMarkerPath(stagingDirectory),
                    package.Archive.Sha256);

                if (Directory.Exists(runtimeDirectory))
                    Directory.Delete(runtimeDirectory, recursive: true);

                Directory.Move(stagingDirectory, runtimeDirectory);
                TryDeleteFile(archivePath);
            }
            catch (InvalidDataException)
            {
                TryDeleteFile(archivePath);
                throw;
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }

            progress?.Report(new ArtifactTransferProgress(
                package.Archive.SizeBytes,
                package.Archive.SizeBytes));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal static RuntimePackage GetRuntimePackage(CrispAsrBackend backend) =>
        backend switch
        {
            CrispAsrBackend.Cpu => CpuRuntime,
            CrispAsrBackend.Cuda => CudaRuntime,
            CrispAsrBackend.Vulkan => VulkanRuntime,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };

    internal static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var destinationPrefix = destinationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedEntryPath = entry.FullName.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Join(destinationRoot, normalizedEntryPath));

            if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Archive entry escapes the destination directory: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private async Task EnsureArtifactAsync(
        RemoteArtifact artifact,
        string destinationPath,
        IProgress<ArtifactTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsArtifactReady(artifact, destinationPath))
        {
            progress?.Report(new ArtifactTransferProgress(artifact.SizeBytes, artifact.SizeBytes));
            return;
        }

        if (await TryAdoptExistingArtifactAsync(artifact, destinationPath, cancellationToken))
        {
            progress?.Report(new ArtifactTransferProgress(artifact.SizeBytes, artifact.SizeBytes));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = $"{destinationPath}.download";
        EnsurePathWithinAssetRoot(destinationPath);
        EnsurePathWithinAssetRoot(temporaryPath);

        try
        {
            await DownloadVerifiedAsync(
                artifact,
                temporaryPath,
                progress,
                cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            File.WriteAllText(GetArtifactMarkerPath(destinationPath), artifact.Sha256);
        }
        catch (InvalidDataException)
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    internal async Task DownloadVerifiedAsync(
        RemoteArtifact artifact,
        string destinationPath,
        IProgress<ArtifactTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var existingLength = File.Exists(destinationPath)
            ? new FileInfo(destinationPath).Length
            : 0;
        if (existingLength > artifact.SizeBytes)
        {
            TryDeleteFile(destinationPath);
            existingLength = 0;
        }

        progress?.Report(new ArtifactTransferProgress(existingLength, artifact.SizeBytes));

        while (existingLength < artifact.SizeBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rangeEnd = Math.Min(
                artifact.SizeBytes - 1,
                existingLength + _downloadChunkSizeBytes - 1);
            Exception? lastError = null;

            for (var attempt = 1; attempt <= _maxDownloadAttempts; attempt++)
            {
                var rangeStart = File.Exists(destinationPath)
                    ? new FileInfo(destinationPath).Length
                    : 0;
                if (rangeStart > rangeEnd)
                {
                    lastError = null;
                    break;
                }

                try
                {
                    await DownloadRangeAsync(
                        artifact,
                        destinationPath,
                        rangeStart,
                        rangeEnd,
                        progress,
                        cancellationToken);
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = new TimeoutException(
                        $"Download of {artifact.FileName} made no progress for {_downloadInactivityTimeout.TotalSeconds:0} seconds.",
                        ex);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastError = ex;
                }

                if (attempt < _maxDownloadAttempts && _downloadRetryDelay > TimeSpan.Zero)
                    await Task.Delay(_downloadRetryDelay, cancellationToken);
            }

            if (lastError is not null)
            {
                throw new IOException(
                    $"Failed to download {artifact.FileName} after {_maxDownloadAttempts} attempts. "
                    + "The partial download was kept and will resume on the next attempt.",
                    lastError);
            }

            existingLength = new FileInfo(destinationPath).Length;
        }

        var actualHash = await ComputeSha256Async(destinationPath, cancellationToken);
        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(destinationPath);
            throw new InvalidDataException(
                $"SHA-256 verification failed for {artifact.FileName}.");
        }

        progress?.Report(new ArtifactTransferProgress(artifact.SizeBytes, artifact.SizeBytes));
    }

    private async Task DownloadRangeAsync(
        RemoteArtifact artifact,
        string destinationPath,
        long rangeStart,
        long rangeEnd,
        IProgress<ArtifactTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var useRange = rangeStart > 0 || artifact.SizeBytes > _downloadChunkSizeBytes;
        var expectedBytes = rangeEnd - rangeStart + 1;

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUrl);
        if (IsHuggingFaceDownload(artifact.DownloadUrl)
            && Volatile.Read(ref _huggingFaceToken) is { Length: > 0 } huggingFaceToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                huggingFaceToken);
        }

        if (useRange)
            request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

        using var inactivityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityCts.CancelAfter(_downloadInactivityTimeout);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            inactivityCts.Token);
        response.EnsureSuccessStatusCode();

        if (useRange)
        {
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new InvalidDataException(
                    $"Server did not honor the byte range for {artifact.FileName}.");
            }

            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange?.From != rangeStart
                || contentRange.To != rangeEnd
                || (contentRange.Length is { } totalLength && totalLength != artifact.SizeBytes))
            {
                throw new InvalidDataException(
                    $"Server returned an unexpected byte range for {artifact.FileName}.");
            }
        }

        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != expectedBytes)
        {
            throw new InvalidDataException(
                $"Unexpected range size for {artifact.FileName}: expected {expectedBytes} bytes, received {contentLength}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(inactivityCts.Token);
        await using var target = new FileStream(
            destinationPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            81_920,
            useAsync: true);

        if (target.Length != rangeStart)
        {
            throw new IOException(
                $"Partial download length changed unexpectedly for {artifact.FileName}.");
        }

        target.Position = rangeStart;
        var buffer = new byte[81_920];
        long written = 0;

        while (written < expectedBytes)
        {
            var bytesToRead = (int)Math.Min(buffer.Length, expectedBytes - written);
            var read = await source.ReadAsync(
                buffer.AsMemory(0, bytesToRead),
                inactivityCts.Token);
            if (read == 0)
            {
                throw new IOException(
                    $"Incomplete byte range for {artifact.FileName}: expected {expectedBytes} bytes, received {written}.");
            }

            inactivityCts.CancelAfter(_downloadInactivityTimeout);
            await target.WriteAsync(buffer.AsMemory(0, read), inactivityCts.Token);
            written += read;
            inactivityCts.CancelAfter(_downloadInactivityTimeout);
            progress?.Report(new ArtifactTransferProgress(
                rangeStart + written,
                artifact.SizeBytes));
        }

        await target.FlushAsync(inactivityCts.Token);
    }

    private static bool IsArtifactReady(RemoteArtifact artifact, string destinationPath)
    {
        var file = new FileInfo(destinationPath);
        if (!file.Exists || file.Length != artifact.SizeBytes)
            return false;

        var markerPath = GetArtifactMarkerPath(destinationPath);
        return File.Exists(markerPath)
            && string.Equals(
                File.ReadAllText(markerPath).Trim(),
                artifact.Sha256,
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryAdoptExistingArtifactAsync(
        RemoteArtifact artifact,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(destinationPath);
        if (!file.Exists || file.Length != artifact.SizeBytes)
            return false;

        var actualHash = await ComputeSha256Async(destinationPath, cancellationToken);
        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;

        File.WriteAllText(GetArtifactMarkerPath(destinationPath), artifact.Sha256);
        return true;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1_048_576,
            useAsync: true);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private string GetRuntimeDirectory(RuntimePackage package) =>
        Path.Join(_assetRoot, "Runtimes", "CrispASR", CrispAsrVersion, package.Id);

    private static string GetArtifactMarkerPath(string destinationPath) =>
        destinationPath + ".sha256";

    private static string GetRuntimeMarkerPath(string runtimeDirectory) =>
        Path.Join(runtimeDirectory, ".typewhisper-runtime.sha256");

    private static string? FindRuntimeExecutable(string runtimeDirectory) =>
        Directory.Exists(runtimeDirectory)
            ? Directory.EnumerateFiles(
                runtimeDirectory,
                "crispasr.exe",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;

    private static bool IsHuggingFaceDownload(string downloadUrl) =>
        Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase);

    private void EnsurePathWithinAssetRoot(string path)
    {
        var rootPrefix = _assetRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Asset path escapes plugin storage: {fullPath}");
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "Cohere Transcribe local inference currently requires Windows x64.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
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
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
        }
    }

    private static bool IsExpectedCleanupFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;
}
