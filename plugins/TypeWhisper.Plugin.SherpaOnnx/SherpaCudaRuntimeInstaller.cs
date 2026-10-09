using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using SharpCompress.Compressors.BZip2;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal interface ISherpaCudaRuntimeInstaller
{
    bool IsInstalled { get; }
    string? RuntimeDirectory { get; }
    Task EnsureInstalledAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The pinned sherpa-onnx CUDA runtime archive and the CUDA wheels that complete it.
/// </summary>
internal sealed record SherpaCudaRuntimePackage(
    string RuntimeVersion,
    string ArchiveUrl,
    string ArchiveSha256,
    IReadOnlyList<CudaDependencyPackage> Dependencies);

/// <summary>
/// A PyPI wheel whose DLLs are copied into the runtime directory, pinned to one file and its SHA-256.
/// </summary>
internal sealed record CudaDependencyPackage(
    string PackageName,
    string Version,
    string WheelUrl,
    string Sha256,
    IReadOnlyList<string> RequiredDlls);

internal sealed class SherpaCudaRuntimeInstaller : ISherpaCudaRuntimeInstaller
{
    // Must match the org.k2fsa.sherpa.onnx package version: this archive replaces its native C API.
    internal const string RuntimeVersion = "v1.13.8";
    internal const string AssetFileName =
        "sherpa-onnx-v1.13.8-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2";
    internal const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/" + AssetFileName;
    // SHA-256 of AssetFileName (595,017,373 bytes); matches the asset digest of the v1.13.8 release.
    internal const string ArchiveSha256 = "066c5b54dbafaa1388001a9c9837ac1374dbba6d6678f193ca06aa0d8e94d8c3";
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

    internal static IReadOnlyList<string> CudnnRuntimeFileNames { get; } =
    [
        "cudnn64_9.dll",
        "cudnn_ops64_9.dll",
        "cudnn_cnn64_9.dll",
        "cudnn_adv64_9.dll",
        "cudnn_graph64_9.dll",
        "cudnn_heuristic64_9.dll",
        "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_precompiled64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll",
        "cudnn_ext64_9.dll"
    ];

    // Each wheel is the single win_amd64 bdist_wheel of its release on PyPI. URL and SHA-256 were
    // taken from https://pypi.org/pypi/<package>/<version>/json (digests.sha256) and are pinned so
    // that nothing is resolved at runtime and every file is verified before it is extracted.
    private static readonly CudaDependencyPackage[] DefaultDependencies =
    [
        // nvidia_cuda_runtime_cu12-12.9.79-py3-none-win_amd64.whl (3,591,604 bytes)
        new(
            "nvidia-cuda-runtime-cu12",
            "12.9.79",
            "https://files.pythonhosted.org/packages/59/df/e7c3a360be4f7b93cee39271b792669baeb3846c58a4df6dfcf187a7ffab/nvidia_cuda_runtime_cu12-12.9.79-py3-none-win_amd64.whl",
            "8e018af8fa02363876860388bd10ccb89eb9ab8fb0aa749aaf58430a9f7c4891",
            ["cudart64_12.dll"]),
        // nvidia_cublas_cu12-12.9.2.10-py3-none-win_amd64.whl (553,162,896 bytes)
        new(
            "nvidia-cublas-cu12",
            "12.9.2.10",
            "https://files.pythonhosted.org/packages/20/e2/fc9a0e985249d873150276d5afb02e39a66817fedbf1a385724393e505ed/nvidia_cublas_cu12-12.9.2.10-py3-none-win_amd64.whl",
            "623f43027d40d44ceadf0043f002bd25cf353e8f13ce90b9a87057019f560661",
            ["cublas64_12.dll", "cublasLt64_12.dll"]),
        // nvidia_cufft_cu12-11.4.1.4-py3-none-win_amd64.whl (200,067,309 bytes)
        new(
            "nvidia-cufft-cu12",
            "11.4.1.4",
            "https://files.pythonhosted.org/packages/20/ee/29955203338515b940bd4f60ffdbc073428f25ef9bfbce44c9a066aedc5c/nvidia_cufft_cu12-11.4.1.4-py3-none-win_amd64.whl",
            "8e5bfaac795e93f80611f807d42844e8e27e340e0cde270dcb6c65386d795b80",
            ["cufft64_11.dll"]),
        // nvidia_cudnn_cu12-9.22.0.52-py3-none-win_amd64.whl (687,235,974 bytes)
        new(
            "nvidia-cudnn-cu12",
            "9.22.0.52",
            "https://files.pythonhosted.org/packages/f2/a4/045f8d0ce6b99726d88e76bbb8ee147123f55e80111d89262762d8149abb/nvidia_cudnn_cu12-9.22.0.52-py3-none-win_amd64.whl",
            "5d10117314c861245992dbcf8a6f8ae1f54852137a7c9f80cc9de9fa596f7d62",
            CudnnRuntimeFileNames)
    ];

    private static readonly SherpaCudaRuntimePackage DefaultPackage = new(
        RuntimeVersion,
        DownloadUrl,
        ArchiveSha256,
        DefaultDependencies);

    private readonly string _runtimeRoot;
    private readonly HttpClient _httpClient;
    private readonly SherpaCudaRuntimePackage _package;
    private readonly string[] _requiredFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Records which verified downloads produced the files in the runtime directory. Only
    // installations performed by a verifying build write it; see IsVerified for how its
    // absence is treated.
    private sealed record RuntimeReceipt(
        string Version,
        string? ArchiveSha256,
        Dictionary<string, string> WheelSha256);

    /// <summary>
    /// Performs sherpa cuda runtime installer.
    /// </summary>
    public SherpaCudaRuntimeInstaller(string pluginDataDirectory, HttpClient httpClient)
        : this(pluginDataDirectory, httpClient, DefaultPackage)
    {
    }

    internal SherpaCudaRuntimeInstaller(
        string pluginDataDirectory,
        HttpClient httpClient,
        SherpaCudaRuntimePackage package)
    {
        var pluginDataRoot = Path.GetFullPath(pluginDataDirectory);
        _runtimeRoot = Path.Join(pluginDataRoot, "Runtimes", "sherpa-onnx-cuda", package.RuntimeVersion);
        _httpClient = httpClient;
        _package = package;
        _requiredFiles = CoreRuntimeFiles
            .Concat(package.Dependencies.SelectMany(dependency => dependency.RequiredDlls))
            .ToArray();
    }

    /// <summary>
    /// Performs runtime directory.
    /// </summary>
    public string RuntimeDirectory => Path.Join(_runtimeRoot, "native");

    private string ReceiptPath => Path.Join(_runtimeRoot, "installed.json");

    /// <summary>
    /// Returns whether installed.
    /// </summary>
    public bool IsInstalled =>
        HasRequiredFiles(_requiredFiles)
        && IsRuntimeImportPatched(GetRuntimeFilePath(SherpaNativeLibraryFileName))
        && IsVerified(ReadReceipt());

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

            // Reaching this point means the files are incomplete or a receipt disagrees with the
            // pins, so anything the receipt does not vouch for is downloaded and verified again.
            var receipt = ReadReceipt() ?? CreateEmptyReceipt();
            if (!HasRequiredFiles(DownloadedRuntimeFiles)
                || !HashMatches(receipt.ArchiveSha256, _package.ArchiveSha256))
            {
                await InstallSherpaRuntimeAsync(cancellationToken);
                receipt = receipt with { ArchiveSha256 = _package.ArchiveSha256 };
                WriteReceipt(receipt);
            }

            EnsureSherpaRuntimeImportAlias(RuntimeDirectory);
            await InstallCudaProviderDependenciesAsync(receipt, cancellationToken);
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
            await DownloadVerifiedAsync(
                _package.ArchiveUrl,
                _package.ArchiveSha256,
                archivePath,
                "sherpa-onnx CUDA runtime",
                cancellationToken);
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

    private async Task InstallCudaProviderDependenciesAsync(
        RuntimeReceipt receipt,
        CancellationToken cancellationToken)
    {
        foreach (var package in _package.Dependencies)
        {
            // A wheel is skipped only when its DLLs exist and the receipt shows they came from
            // the pinned file; the receipt is updated per wheel so an interrupted installation
            // resumes with the remaining wheels instead of downloading everything again.
            if (HasRequiredFiles(package.RequiredDlls)
                && receipt.WheelSha256.TryGetValue(package.PackageName, out var installedSha256)
                && HashMatches(installedSha256, package.Sha256))
            {
                continue;
            }

            var wheelPath = Path.Join(
                _runtimeRoot,
                $"{package.PackageName}-{package.Version}.{Guid.NewGuid():N}.whl.tmp");

            try
            {
                await DownloadVerifiedAsync(
                    package.WheelUrl,
                    package.Sha256,
                    wheelPath,
                    $"CUDA dependency package {package.PackageName} {package.Version}",
                    cancellationToken);
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

            receipt.WheelSha256[package.PackageName] = package.Sha256;
            WriteReceipt(receipt);
        }
    }

    private async Task DownloadVerifiedAsync(
        string url,
        string expectedSha256,
        string destinationPath,
        string description,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        string actualSha256;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            // Hash while writing: the archives are hundreds of megabytes each, so reading them
            // back from disk only to hash them would double the I/O of every installation.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            actualSha256 = Convert.ToHexString(hasher.GetHashAndReset());
        }

        if (HashMatches(actualSha256, expectedSha256))
            return;

        // Nothing is extracted from an unverified download, and it must not linger for a retry to pick up.
        TryDeleteFile(destinationPath);
        throw new InvalidOperationException(
            $"The downloaded {description} did not match the expected checksum.");
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        // The pinned asset is a BZip2-compressed TAR, which SharpCompress's archive detection does
        // not open. Decode the BZip2 layer explicitly; only DLLs are needed from the archive.
        var root = Path.GetFullPath(destinationDirectory);
        using var compressed = File.OpenRead(archivePath);
        using var decompressed = BZip2Stream.Create(
            compressed,
            SharpCompress.Compressors.CompressionMode.Decompress,
            false,
            leaveOpen: true);
        using var reader = new TarReader(decompressed);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || entry.DataStream is null)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Join(root, entry.Name));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe entry in the sherpa-onnx CUDA runtime archive.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            entry.DataStream.CopyTo(output);
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

    // Returns null when there is no usable receipt. An unreadable one is treated the same way: it
    // can only come from an interrupted write by a verifying build, which never leaves unverified
    // files behind, so reading it as a mismatch would only force a needless re-download.
    private RuntimeReceipt? ReadReceipt()
    {
        if (!File.Exists(ReceiptPath))
            return null;

        try
        {
            var receipt = JsonSerializer.Deserialize<RuntimeReceipt>(File.ReadAllText(ReceiptPath));
            if (receipt is { WheelSha256: not null } && receipt.Version == _package.RuntimeVersion)
                return receipt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Ignoring unreadable sherpa-onnx CUDA runtime receipt '{ReceiptPath}': {ex.Message}");
        }

        return null;
    }

    private RuntimeReceipt CreateEmptyReceipt() =>
        new(_package.RuntimeVersion, null, new Dictionary<string, string>(StringComparer.Ordinal));

    private void WriteReceipt(RuntimeReceipt receipt) =>
        File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(receipt));

    private bool IsVerified(RuntimeReceipt? receipt)
    {
        // A complete installation without a receipt predates download verification. Its files came
        // from the same pinned URLs, but the archives are gone and sherpa-onnx-c-api.dll is patched
        // in place, so they cannot be checked after the fact. They are kept as they are and nothing
        // is written: re-downloading 1.75 GB on the explicit CUDA preference, or silently falling
        // back to the CPU on Auto where the installer never runs, would be worse than trusting a
        // working GPU setup. Only a receipt that exists and disagrees with the pins, which means a
        // real version change, sends the installation back through the installer.
        if (receipt is null)
            return true;

        return HashMatches(receipt.ArchiveSha256, _package.ArchiveSha256)
            && _package.Dependencies.All(dependency =>
                receipt.WheelSha256.TryGetValue(dependency.PackageName, out var sha256)
                && HashMatches(sha256, dependency.Sha256));
    }

    private static bool HashMatches(string? actualSha256, string expectedSha256) =>
        string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

    private bool HasRequiredFiles(IEnumerable<string> files) =>
        files.All(file => File.Exists(GetRuntimeFilePath(file)));

    private void ValidateInstalledRuntime()
    {
        var missing = _requiredFiles
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
}
