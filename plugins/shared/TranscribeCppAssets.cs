using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Shared;

/// <summary>
/// One pinned download. A file asset is stored as <see cref="FileName"/>; an archive asset is a tar.gz whose
/// <see cref="ArchiveRoot"/> directory is unpacked, keeping only DLLs, contract.json and license texts.
/// </summary>
internal sealed record TranscribeCppAsset(string Name, string Url, string Sha256, long Size, string FileName, string? ArchiveRoot = null)
{
    // transcribe.cpp's release tarball for Windows x64 with the CPU and Vulkan backends; 0.3.1 of 2026-10-04.
    internal static readonly TranscribeCppAsset Runtime = new("transcribe.cpp runtime",
        "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.3.1/transcribe-native-0.3.1-windows-x86_64-cpu-vulkan.tar.gz",
        "d06229a1e854f0cbbf90cb547bb6c7dd14d9bab16ca53b808493ad8eaa55ffa6", 17449854, "transcribe.dll",
        "transcribe-native-windows-x86_64-cpu-vulkan");

    internal bool IsArchive => ArchiveRoot is not null;
}

/// <summary>Downloads, verifies and installs one asset into its own directory, publishing it only when complete.</summary>
internal sealed class TranscribeCppAssetStore(HttpClient http, TranscribeCppAsset asset, Func<string, long?>? availableBytes = null)
{
    // The runtime unpacks to about 53 MB; anything far beyond that is not the pinned archive.
    private const long MaximumExtractedBytes = 200_000_000;

    internal TranscribeCppAsset Asset => asset;

    internal bool IsReady(string directory)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(Path.Join(directory, "ready.json")));
            return receipt is { Files: { Count: > 0 } files } && receipt.Sha256 == asset.Sha256 && files.All(pair =>
                new FileInfo(Path.Join(directory, pair.Key)) is { Exists: true } file && file.Length == pair.Value)
                && files.ContainsKey(asset.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal async Task DownloadAsync(string directory, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsReady(directory)) { progress?.Report(1); return; }
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);
        RemoveAbandonedStaging(directory);
        ModelStorageSpace.EnsureAvailable(parent, asset.Size * (asset.IsArchive ? 4 : 1), asset.Name, availableBytes);
        var staging = directory + ".download-" + Guid.NewGuid().ToString("N");
        var content = Path.Join(staging, "content");
        Directory.CreateDirectory(content);
        try
        {
            var download = Path.Join(staging, asset.IsArchive ? "asset.tar.gz" : asset.FileName);
            await DownloadVerifiedAsync(download, progress, ct).ConfigureAwait(false);
            if (asset.IsArchive) await ExtractAsync(download, content, asset.ArchiveRoot!, ct).ConfigureAwait(false);
            else File.Move(download, Path.Join(content, asset.FileName));
            if (!File.Exists(Path.Join(content, asset.FileName)))
                throw new InvalidDataException($"{asset.Name} does not contain {asset.FileName}.");
            var files = Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(content, path).Replace('\\', '/'), path => new FileInfo(path).Length);
            await File.WriteAllTextAsync(Path.Join(content, "ready.json"), JsonSerializer.Serialize(new Receipt(asset.Sha256, files)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.Move(content, directory);
            progress?.Report(1);
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadVerifiedAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException($"Unexpected {asset.Name} download size.");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > asset.Size) throw new InvalidDataException($"{asset.Name} is larger than expected.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                progress?.Report(.97 * total / asset.Size);
            }
            if (total != asset.Size) throw new InvalidDataException($"Incomplete {asset.Name} download.");
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{asset.Name} checksum mismatch. The download was not installed.");
    }

    /// <summary>Unpacks the runtime's DLLs, contract.json and licenses from below <paramref name="root"/>; rejects unsafe entries.</summary>
    internal static async Task ExtractAsync(string archive, string destination, string root, CancellationToken ct)
    {
        await using var input = File.OpenRead(archive);
        await using var decompressed = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(decompressed);
        long expanded = 0;
        var prefix = root + "/";
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var key = entry.Name.Replace('\\', '/');
            if (key.StartsWith('/') || key.Contains(':') || key.Split('/').Contains("..") ||
                entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Unsafe transcribe.cpp archive entry: " + key);
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExtractedBytes) throw new InvalidDataException("The transcribe.cpp archive exceeds its extraction limit.");
            if (entry.EntryType == TarEntryType.Directory || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = key[prefix.Length..];
            var keep = relative.StartsWith("licenses/", StringComparison.Ordinal)
                || (!relative.Contains('/') && (relative == "contract.json" || relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            if (!keep || entry.DataStream is null) continue;
            var path = Path.Join(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await entry.DataStream.CopyToAsync(output, ct).ConfigureAwait(false);
            if (output.Length != entry.Length) throw new InvalidDataException("Incomplete transcribe.cpp archive entry.");
        }
    }

    // An interrupted download leaves its staging directory behind; one still being written is kept.
    internal static void RemoveAbandonedStaging(string directory)
    {
        var parent = Path.GetDirectoryName(directory)!;
        if (!Directory.Exists(parent)) return;
        var prefix = Path.GetFileName(directory) + ".download-";
        foreach (var staging in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(staging)[prefix.Length..], "N", out _)
                || (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0) continue;
            try { Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Receipt(string Sha256, Dictionary<string, long> Files);
}
