using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace TypeWhisper.Plugin.Qwen3Local;

internal sealed record QwenAssetSource(string Url, string Sha256, long Size);

internal sealed class QwenModelAssets(HttpClient http, QwenAssetSource? source = null)
{
    internal static readonly QwenAssetSource Official = new(
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2",
        "393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96", 878702423);
    internal static readonly string[] RequiredFiles = ["conv_frontend.onnx", "encoder.int8.onnx",
        "decoder.int8.onnx", "tokenizer/merges.txt", "tokenizer/vocab.json", "tokenizer/tokenizer_config.json"];
    private readonly QwenAssetSource _source = source ?? Official;

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
        var staging = directory + ".download-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "model.tar.bz2");
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
            var extracted = Path.Combine(staging, "model");
            Directory.CreateDirectory(extracted);
            await Task.Run(() => ExtractAsync(archive, extracted, ct, progress), ct).ConfigureAwait(false);
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

    private static async Task<long> CopyAsync(Stream input, Stream output, long maximum, CancellationToken ct, Action<long>? progress = null)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Qwen download or extraction exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            progress?.Invoke(total);
        }
        return total;
    }

    private sealed record Receipt(string Sha256, Dictionary<string, long> Files);
}
