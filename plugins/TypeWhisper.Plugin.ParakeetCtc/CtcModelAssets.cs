using System.Net.Http;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace TypeWhisper.Plugin.ParakeetCtc;

public sealed record CtcAssetSource(string Url, string Sha256, long MaximumBytes);

/// <summary>Prepares verified CTC assets without publishing partial downloads or extracted files.</summary>
public sealed class CtcModelAssets(HttpClient http, CtcAssetSource? archive = null, CtcAssetSource? tokenizer = null)
{
    public static CtcAssetSource Archive { get; } = new(
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2",
        "17f945007b52ccd8b7200ffc7c5652e9e8e961dfdf479cefcabd06cf5703630b", 512L * 1024 * 1024);
    public static CtcAssetSource Tokenizer { get; } = new(
        "https://huggingface.co/FluidInference/parakeet-ctc-110m-coreml/resolve/main/tokenizer.json",
        "9f7c517c0bf644b1b690ab037bab4d4c53aecd38e047e7154d011013ab9160db", 16L * 1024 * 1024);
    private readonly CtcAssetSource _archive = archive ?? Archive;
    private readonly CtcAssetSource _tokenizer = tokenizer ?? Tokenizer;
    private static readonly string[] Required = ["model.int8.onnx", "tokens.txt", "tokenizer.json"];
    private sealed record Receipt(string ArchiveHash, string TokenizerHash, Dictionary<string, string> Files);

    public async Task EnsureAsync(string directory, CancellationToken cancellationToken, Action<string>? report = null)
    {
        directory = Path.GetFullPath(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);
        await using var lease = await AcquireLockAsync(Path.Combine(parent, ".ctc-assets.lock"), ct).ConfigureAwait(false);
        if (await IsVerifiedAsync(directory, ct).ConfigureAwait(false)) return;
        var staging = Path.Combine(parent, ".ctc-staging-" + Guid.NewGuid().ToString("N"));
        var ready = Path.Combine(staging, "model");
        Directory.CreateDirectory(ready);
        try
        {
            report?.Invoke("Downloading NVIDIA dictionary boosting model…");
            var archivePath = Path.Combine(staging, "model.archive");
            await DownloadAsync(_archive, archivePath, ct).ConfigureAwait(false);
            report?.Invoke("Verifying and extracting dictionary boosting model…");
            await ExtractAsync(archivePath, ready, ct).ConfigureAwait(false);
            report?.Invoke("Downloading dictionary tokenizer…");
            await DownloadAsync(_tokenizer, Path.Combine(ready, "tokenizer.json"), ct).ConfigureAwait(false);
            if (new CtcTokenizer(Path.Combine(ready, "tokens.txt")).BlankId != 1024)
                throw new InvalidDataException("The downloaded tokenizer does not match the CTC model.");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in Required) hashes[name] = await HashAsync(Path.Combine(ready, name), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(ready, "verified-assets.json"),
                JsonSerializer.Serialize(new Receipt(_archive.Sha256, _tokenizer.Sha256, hashes)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Publish(ready, directory, report);
            report?.Invoke("Dictionary boosting assets are ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Dictionary boosting setup timed out. Check your connection and enable NVIDIA again to retry."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new IOException("Dictionary boosting setup failed. Check your connection and available disk space, then enable NVIDIA again to retry. " + ex.Message, ex); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private async Task<bool> IsVerifiedAsync(string directory, CancellationToken ct)
    {
        var receiptPath = Path.Combine(directory, "verified-assets.json");
        if (!File.Exists(receiptPath)) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(receiptPath, ct).ConfigureAwait(false));
            if (receipt is null || receipt.ArchiveHash != _archive.Sha256 || receipt.TokenizerHash != _tokenizer.Sha256 || receipt.Files is null) return false;
            foreach (var name in Required)
                if (!File.Exists(Path.Combine(directory, name)) || !receipt.Files.TryGetValue(name, out var hash)
                    || !string.Equals(hash, await HashAsync(Path.Combine(directory, name), ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private async Task DownloadAsync(CtcAssetSource source, string destination, CancellationToken ct)
    {
        using var response = await http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is <= 0 || length > source.MaximumBytes) throw new InvalidDataException("Unexpected CTC asset download size.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var total = await CopyBoundedAsync(input, output, source.MaximumBytes, ct).ConfigureAwait(false);
            if (total == 0 || length.HasValue && total != length.Value) throw new InvalidDataException("The CTC download is incomplete.");
        }
        if (!string.Equals(source.Sha256, await HashAsync(destination, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CTC asset checksum mismatch; the download was not installed.");
    }

    private static async Task ExtractAsync(string archivePath, string destination, CancellationToken ct)
    {
        // The pinned artifact is a BZip2-compressed TAR. Archive auto-detection can
        // identify TAR without decoding its BZip2 layer. Decode explicitly and
        // bound the entire expanded archive, including entries we do not install.
        var tarPath = archivePath + ".tar";
        await using (var compressed = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        using (var decompressed = BZip2Stream.Create(compressed, CompressionMode.Decompress, false, leaveOpen: true))
        await using (var expanded = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await CopyBoundedAsync(decompressed, expanded, 600L * 1024 * 1024, ct).ConfigureAwait(false);
        await using var tar = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var reader = new TarReader(tar, leaveOpen: true);
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var key = entry.Name.Replace('\\', '/');
            if (key.StartsWith('/') || key.Contains(':') || key.Split('/').Contains("..") ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new InvalidDataException("Unsafe entry in CTC model archive.");
            if (entry.EntryType == TarEntryType.Directory) continue;
            var name = key.Split('/').Last();
            if (name is not ("model.int8.onnx" or "tokens.txt")) continue;
            if (!found.Add(name)) throw new InvalidDataException("Duplicate CTC model archive entry.");
            var maximum = name == "tokens.txt" ? 4L * 1024 * 1024 : 512L * 1024 * 1024;
            if (entry.Length <= 0 || entry.Length > maximum || entry.DataStream is null) throw new InvalidDataException("Unexpected extracted CTC model size.");
            var input = entry.DataStream;
            await using var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            if (await CopyBoundedAsync(input, output, maximum, ct).ConfigureAwait(false) != entry.Length)
                throw new InvalidDataException("Incomplete CTC model archive entry.");
        }
        if (found.Count != 2) throw new InvalidDataException("The CTC model archive is missing required files.");
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("The CTC asset exceeded its download or extraction size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
    }

    private static void Publish(string ready, string destination, Action<string>? report)
    {
        var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        var previous = Directory.Exists(destination);
        var owned = previous && File.Exists(Path.Combine(destination, "verified-assets.json"))
            && !Directory.EnumerateDirectories(destination).Any()
            && Directory.EnumerateFiles(destination).All(path => Required.Contains(Path.GetFileName(path)) || Path.GetFileName(path) == "verified-assets.json");
        if (previous) Directory.Move(destination, backup);
        try { Directory.Move(ready, destination); }
        catch
        {
            if (previous) Directory.Move(backup, destination);
            throw;
        }
        if (previous && owned)
        {
            try { Directory.Delete(backup, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { report?.Invoke("Dictionary assets are installed; an older backup could not be removed: " + backup); }
        }
        else if (previous) report?.Invoke("Previous manually supplied dictionary assets were retained at " + backup);
    }
}
