using System.Security.Cryptography;
using System.Text.Json;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>One pinned model file.</summary>
/// <param name="FileName">The file name in the model directory.</param>
/// <param name="DownloadUrl">The pinned download URL.</param>
/// <param name="SizeBytes">The exact size.</param>
/// <param name="Sha256">The SHA-256 of the exact bytes, in hexadecimal.</param>
internal sealed record NemotronModelFile(string FileName, string DownloadUrl, long SizeBytes, string Sha256) : IDownloadableModel;

/// <summary>
/// The fp32 ONNX export of NVIDIA Nemotron 3 Diarization by onnx-community, pinned to one Hugging Face revision and
/// verified by size and SHA-256. The DirectML graph is derived from the downloaded one on this device.
/// </summary>
internal sealed class NemotronModelAssets
{
    internal const string Repository = "onnx-community/Nemotron-3-Diarization-ONNX";
    internal const string Revision = "353b6f8ad2cac3580e982d7fbdf0a010786b0406";
    internal const string CpuModelName = "model.onnx";
    internal const string DirectMlModelName = "model.dml.onnx";
    internal const string DirectMlModelSha256 = "9b59b9793505479e7403dc8575d3abaa75783c02c73e03dfc6c21748d4849987";
    private const string BaseUrl = "https://huggingface.co/" + Repository + "/resolve/" + Revision + "/onnx/";
    private const string MarkerName = "verified.json";

    internal static readonly IReadOnlyList<NemotronModelFile> DefaultFiles =
    [
        new(CpuModelName, BaseUrl + "model.onnx", 303_466, "12a7c98fc1ee6ec98ea728c7ffd9fcf6b245f3c9c1ea9f7c2a7cc47c023e3f9d"),
        new("model.onnx_data", BaseUrl + "model.onnx_data", 398_184_448, "c293d9b5930eb9f6172f095ced052c0d1bbdbeb2594a115497583ca209b1dbd6")
    ];

    private readonly IReadOnlyList<NemotronModelFile> _files;
    private readonly string? _directMlSha256;

    /// <param name="root">The plugin's asset directory; the model lives in a folder named after its revision.</param>
    /// <param name="files">The pinned files; tests pass their own.</param>
    /// <param name="directMlSha256">The expected SHA-256 of the DirectML graph, or null to skip the check (tests).</param>
    internal NemotronModelAssets(string root, IReadOnlyList<NemotronModelFile>? files = null, string? directMlSha256 = DirectMlModelSha256)
    {
        Directory = Path.Join(root, "nemotron-3-diarization-" + Revision[..12]);
        _files = files ?? DefaultFiles;
        _directMlSha256 = directMlSha256;
    }

    internal string Directory { get; }
    internal string CpuModel => Path.Join(Directory, CpuModelName);
    internal string DirectMlModel => Path.Join(Directory, DirectMlModelName);
    internal long TotalBytes => _files.Sum(file => file.SizeBytes);

    /// <summary>Every file is present with its pinned size and was verified after its download.</summary>
    internal bool IsInstalled =>
        File.Exists(Path.Join(Directory, MarkerName))
        && _files.All(file => new FileInfo(Path.Join(Directory, file.FileName)) is { Exists: true } info && info.Length == file.SizeBytes);

    /// <summary>Downloads the missing files, resuming partial ones, and verifies each by size and SHA-256.</summary>
    internal async Task DownloadAsync(HttpClient client, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (IsInstalled) { progress?.Report(1); return; }
        System.IO.Directory.CreateDirectory(Directory);
        File.Delete(Path.Join(Directory, MarkerName));
        var total = (double)TotalBytes;
        long done = 0;
        foreach (var file in _files)
        {
            var destination = Path.Join(Directory, file.FileName);
            var completed = done;
            var fileProgress = progress is null ? null : new Relay(value => progress.Report(Math.Min(0.99, (completed + (value * file.SizeBytes)) / total)));
            if (!await IsVerifiedAsync(destination, file, cancellationToken).ConfigureAwait(false))
                await ResumableModelDownloader.DownloadAsync(client, file, destination, fileProgress, cancellationToken).ConfigureAwait(false);
            done += file.SizeBytes;
        }

        await File.WriteAllTextAsync(Path.Join(Directory, MarkerName), JsonSerializer.Serialize(new
        {
            repository = Repository, revision = Revision, files = _files.Select(file => new { file.FileName, file.SizeBytes, file.Sha256 })
        }), cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }

    /// <summary>
    /// Writes the DirectML graph next to the CPU graph once and returns its path. The graph is checked against its
    /// pinned SHA-256, so a changed rewrite cannot run unnoticed.
    /// </summary>
    internal string EnsureDirectMlModel()
    {
        var target = DirectMlModel;
        if (File.Exists(target) && (_directMlSha256 is null || Hash(File.ReadAllBytes(target)) == _directMlSha256)) return target;
        var rewritten = OnnxGeluRewrite.Rewrite(File.ReadAllBytes(CpuModel), out var replaced);
        if (replaced == 0) throw new InvalidDataException("The diarization model has no Gelu nodes to rewrite.");
        if (_directMlSha256 is not null && Hash(rewritten) != _directMlSha256)
            throw new InvalidDataException("The DirectML variant of the diarization model does not match its expected checksum.");
        var partial = target + ".tmp";
        File.WriteAllBytes(partial, rewritten);
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Removes the model directory.</summary>
    internal void Delete()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static async Task<bool> IsVerifiedAsync(string path, NemotronModelFile file, CancellationToken cancellationToken)
    {
        if (new FileInfo(path) is not { Exists: true } info || info.Length != file.SizeBytes) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
