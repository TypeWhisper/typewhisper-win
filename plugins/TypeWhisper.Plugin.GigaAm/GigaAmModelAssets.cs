using System.Security.Cryptography;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.GigaAm;

/// <summary>One model file, pinned to the size and SHA-256 it has at the pinned revision.</summary>
internal sealed record GigaAmModelFile(string Name, long Size, string Sha256);

/// <summary>Downloads the GigaAM v3 model files and publishes each one only after its hash matched.</summary>
internal sealed class GigaAmModelAssets(HttpClient http, string baseUrl, IReadOnlyList<GigaAmModelFile> files,
    Func<string, long?>? availableBytes = null)
{
    // The URLs name a commit instead of main because native code parses the files and main can serve different bytes
    // under the same address. Pinned on 2026-10-21 to the newest commit of istupakov/gigaam-v3-onnx (2026-02-18): the
    // SHA-256 values are the LFS object ids from https://huggingface.co/api/models/istupakov/gigaam-v3-onnx/tree/<commit>;
    // the vocabulary, which is not stored in LFS, was hashed from a download of that commit.
    internal const string Revision = "322c3b29492673eb7d0b434bfa9dfb8653e34d02";
    internal const string DefaultBaseUrl = "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/" + Revision;

    internal static readonly IReadOnlyList<GigaAmModelFile> DefaultFiles =
    [
        new(GigaAmModelFiles.Encoder, 224_570_477, "4e0e076a6076cd110277e529b8ac8f32cd5297f7fbebad5341ae8ddb7d00817b"),
        new(GigaAmModelFiles.Decoder, 1_159_170, "89014e134865615b91e037157e46e389b1271e6072460efc010ea08e61e23146"),
        new(GigaAmModelFiles.Joint, 687_791, "ade116563dbf66e503b0994efab6b5861412743e52bf31c39fc3fffa3783d5d1"),
        new(GigaAmModelFiles.Vocabulary, 13_354, "39abae20e692998290c574e606f11a9edef2902a1995463fcff63d1490cf22b7")
    ];

    // A download is verified under this suffix and only renamed to its final name once its hash matches.
    private const string UnverifiedSuffix = ".unverified";

    internal IReadOnlyList<GigaAmModelFile> Files => files;

    internal long TotalBytes => files.Sum(file => file.Size);

    /// <summary>Whether every file is present at its final name with its pinned size.</summary>
    internal bool IsReady(string directory) => files.All(file => IsPresent(directory, file));

    internal async Task DownloadAsync(string directory, string modelName, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var missing = files.Where(file => !IsPresent(directory, file)).ToList();
        foreach (var file in missing)
        {
            // ".tmp" is the downloader's partial file, ".unverified" a complete download that was never checked;
            // neither counts as the model file.
            var path = Path.Join(directory, file.Name);
            foreach (var abandoned in new[] { path, path + UnverifiedSuffix + ".tmp", path + UnverifiedSuffix })
                ModelStorageSpace.TryRemoveAbandonedFile(abandoned);
        }
        ModelStorageSpace.EnsureAvailable(directory, missing.Sum(file => file.Size), modelName, availableBytes);

        double total = TotalBytes;
        double completed = files.Except(missing).Sum(file => file.Size);
        progress?.Report(completed / total);
        foreach (var file in missing)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Join(directory, file.Name);
            var unverified = destination + UnverifiedSuffix;
            var start = completed;
            await ModelFileDownloader.DownloadAsync(http, baseUrl + "/" + file.Name, unverified,
                new Progress(value => progress?.Report((start + value * file.Size) / total)), ct).ConfigureAwait(false);
            await VerifyAsync(file, unverified, ct).ConfigureAwait(false);
            File.Move(unverified, destination, overwrite: true);
            completed += file.Size;
            progress?.Report(completed / total);
        }
    }

    private static bool IsPresent(string directory, GigaAmModelFile file) =>
        new FileInfo(Path.Join(directory, file.Name)) is { Exists: true } info && info.Length == file.Size;

    private static async Task VerifyAsync(GigaAmModelFile file, string path, CancellationToken ct)
    {
        string actual;
        long length;
        await using (var stream = File.OpenRead(path))
        {
            length = stream.Length;
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
        if (length == file.Size && string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(path);
        throw new InvalidDataException($"The downloaded {file.Name} does not match its expected checksum.");
    }

    // A synchronous reporter: Progress<T> would post to a captured context and could report out of order.
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
