using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Qwen3Local;

internal sealed record QwenAssetFile(string Name, string Sha256, long Size);

// An archive source is one tar.bz2; ExtractedSize covers the model files unpacked next to it before the verified
// set is moved into place. A file source downloads each pinned file from Url/<name>; its Sha256 identifies the set.
internal sealed record QwenAssetSource(string Name, string Url, string Sha256, long Size, long ExtractedSize = 0,
    IReadOnlyList<QwenAssetFile>? Files = null)
{
    internal static QwenAssetSource FromFiles(string name, string baseUrl, params QwenAssetFile[] files) => new(name, baseUrl,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(file => $"{file.Name} {file.Sha256} {file.Size}"))))),
        files.Sum(file => file.Size), Files: files);
}

internal sealed class QwenModelAssets(HttpClient http, QwenAssetSource source, Func<string, long?>? availableBytes = null)
{
    internal static readonly QwenAssetSource Model06B = new("Qwen3-ASR 0.6B",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2",
        "393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96", 878702423, 1_100_000_000);
    // No official sherpa-onnx 1.7B build exists. This community export uses the converter of the 0.6B archive;
    // its revision and every file hash are pinned, so a changed upload is never installed.
    internal static readonly QwenAssetSource Model17B = QwenAssetSource.FromFiles("Qwen3-ASR 1.7B",
        "https://huggingface.co/thieunv-asilla/sherpa-onnx-qwen3-asr-1.7B-int8/resolve/69eb686fd94a4a865bb5340a3d6ac0d7f1fec0d5",
        new("conv_frontend.onnx", "3cb27a9fe94d95c938e476f2012b21aba2ec0bfceef33b0e58acd208946bafdd", 48080441),
        new("encoder.int8.onnx", "a5deedae034ece715de8ed204378d8c77f889af3a60c2566581135e84cced7cd", 314222162),
        new("decoder.int8.onnx", "c43c853fa6e97d08365cb8a5502b360b595cd43c00dc60e4d8ca7cc18cad460b", 2037458645),
        new("tokenizer/merges.txt", "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5", 1671853),
        new("tokenizer/vocab.json", "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910", 2776833),
        new("tokenizer/tokenizer_config.json", "4942d005604266809309cabc9f4e9cb89ce855d59b14681fdc0e1cc62ea26c4c", 12487));
    internal static readonly string[] RequiredFiles = ["conv_frontend.onnx", "encoder.int8.onnx",
        "decoder.int8.onnx", "tokenizer/merges.txt", "tokenizer/vocab.json", "tokenizer/tokenizer_config.json"];
    private readonly QwenAssetSource _source = source;

    internal bool IsReady(string directory)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(Path.Combine(directory, "ready.json")));
            return receipt?.Sha256 == _source.Sha256 && receipt.Files is not null && RequiredFiles.All(name =>
                receipt.Files.TryGetValue(name, out var size) && size > 0 &&
                new FileInfo(Path.Combine(directory, name)) is { Exists: true } file && file.Length == size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal async Task DownloadAsync(string directory, IProgress<double>? progress, CancellationToken ct)
    {
        if (IsReady(directory)) { progress?.Report(1); return; }
        RemoveAbandonedStaging(directory);
        ModelStorageSpace.EnsureAvailable(Path.GetDirectoryName(directory)!, _source.Size + _source.ExtractedSize,
            _source.Name, availableBytes);
        var staging = directory + ".download-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var extracted = Path.Combine(staging, "model");
            Directory.CreateDirectory(extracted);
            if (_source.Files is { } files) await DownloadFilesAsync(files, extracted, progress, ct).ConfigureAwait(false);
            else await DownloadArchiveAsync(staging, extracted, progress, ct).ConfigureAwait(false);
            progress?.Report(.98);
            var receipt = new Receipt(_source.Sha256, RequiredFiles.ToDictionary(name => name,
                name => new FileInfo(Path.Combine(extracted, name)).Length));
            await File.WriteAllTextAsync(Path.Combine(extracted, "ready.json"), JsonSerializer.Serialize(receipt), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Only a verified complete set becomes visible to the model manager.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.Move(extracted, directory);
            progress?.Report(1);
        }
        finally { Directory.Delete(staging, true); }
    }

    private async Task DownloadArchiveAsync(string staging, string extracted, IProgress<double>? progress, CancellationToken ct)
    {
        var archive = Path.Join(staging, "model.tar.bz2");
        using (var response = await http.GetAsync(_source.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } size && size != _source.Size)
                throw new InvalidDataException("Unexpected Qwen model download size.");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            if (await CopyAsync(input, output, _source.Size, ct, bytes => progress?.Report(.85 * bytes / _source.Size)).ConfigureAwait(false) != _source.Size)
                throw new InvalidDataException("Incomplete Qwen model download.");
        }
        await using (var file = File.OpenRead(archive))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!hash.Equals(_source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Qwen model checksum mismatch. The download was not installed.");
        }
        await Task.Run(() => ExtractAsync(archive, extracted, ct, progress), ct).ConfigureAwait(false);
    }

    private async Task DownloadFilesAsync(IReadOnlyList<QwenAssetFile> files, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        long completed = 0;
        foreach (var file in files)
        {
            var path = Path.Join(destination, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await http.GetAsync(_source.Url + "/" + file.Name, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } size && size != file.Size)
                    throw new InvalidDataException("Unexpected Qwen model download size.");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                if (await CopyAsync(input, output, file.Size, ct, bytes => progress?.Report(.98 * (completed + bytes) / _source.Size), hash).ConfigureAwait(false) != file.Size)
                    throw new InvalidDataException("Incomplete Qwen model download.");
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Qwen model checksum mismatch. The download was not installed.");
            completed += file.Size;
        }
    }

    // An interrupted download leaves its staging directory behind; a download still writing a file is kept.
    internal static void RemoveAbandonedStaging(string directory)
    {
        var parent = Path.GetDirectoryName(directory)!;
        if (!Directory.Exists(parent)) return;
        var prefix = Path.GetFileName(directory) + ".download-";
        var abandoned = Directory.EnumerateDirectories(parent, prefix + "*").Where(staging =>
            Guid.TryParseExact(Path.GetFileName(staging)[prefix.Length..], "N", out _)
            && (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0);
        foreach (var staging in abandoned)
        {
            if (Directory.EnumerateFiles(staging, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Any(IsInUse)) continue;
            try { Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine("Abandoned Qwen staging directory could not be removed: " + ex.GetType().Name); }
        }
    }

    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct, IProgress<double>? progress = null)
    {
        using var input = File.OpenRead(archive);
        using var decompressed = BZip2Stream.Create(input, CompressionMode.Decompress, false, leaveOpen: true);
        using var tar = new TarReader(decompressed);
        var found = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        long installed = 0;
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var key = entry.Name.Replace('\\', '/');
            if (key.StartsWith('/') || key.Contains(':') || key.Split('/').Contains("..") ||
                entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Unsafe Qwen model archive entry.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 1_500_000_000) throw new InvalidDataException("Qwen model archive exceeds its extraction limit.");
            if (entry.EntryType == TarEntryType.Directory) continue;
            var relative = key[(key.IndexOf('/') + 1)..];
            if (!RequiredFiles.Contains(relative, StringComparer.Ordinal)) continue;
            if (!found.Add(relative) || entry.Length <= 0 || entry.DataStream is null)
                throw new InvalidDataException("Duplicate or incomplete Qwen model file.");
            var maximum = relative.StartsWith("tokenizer/", StringComparison.Ordinal) ? 20_000_000 : 1_000_000_000;
            if (entry.Length > maximum) throw new InvalidDataException("Qwen model file exceeds its size limit.");
            var path = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            if (await CopyAsync(entry.DataStream, output, maximum, ct,
                bytes => progress?.Report(.85 + .13 * Math.Min(1, (installed + bytes) / 1_100_000_000d))).ConfigureAwait(false) != entry.Length)
                throw new InvalidDataException("Incomplete Qwen archive entry.");
            installed += entry.Length;
        }
        if (found.Count != RequiredFiles.Length) throw new InvalidDataException("Qwen model archive is missing required files.");
    }

    // An active writer holds its file exclusively, so only files of an abandoned download can be opened here.
    private static bool IsInUse(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static async Task<long> CopyAsync(Stream input, Stream output, long maximum, CancellationToken ct, Action<long>? progress = null,
        IncrementalHash? hash = null)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Qwen download or extraction exceeded its size limit.");
            hash?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            progress?.Invoke(total);
        }
        return total;
    }

    private sealed record Receipt(string Sha256, Dictionary<string, long> Files);
}
