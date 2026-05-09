using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Stt;

internal sealed class Qwen3ModelStore(HttpClient httpClient, Func<string?> getHuggingFaceToken, Action<PluginLogLevel, string> log)
{
    public string GetModelDirectory(string pluginDataDirectory, string modelId)
    {
        var overrideDir = Environment.GetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;

        return Path.Combine(pluginDataDirectory, "Models", Qwen3ModelCatalog.NormalizeModelId(modelId));
    }

    public bool IsModelDownloaded(string pluginDataDirectory, string modelId)
    {
        var model = Qwen3ModelCatalog.GetModel(modelId);
        var dir = GetModelDirectory(pluginDataDirectory, model.Id);
        return model.RequiredFiles.All(file => File.Exists(Path.Combine(dir, file)));
    }

    public async Task DownloadModelAsync(
        string pluginDataDirectory,
        string modelId,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var model = Qwen3ModelCatalog.GetModel(modelId);
        var dir = GetModelDirectory(pluginDataDirectory, model.Id);
        Directory.CreateDirectory(dir);

        if (model.RequiredFiles.All(file => File.Exists(Path.Combine(dir, file))))
        {
            progress?.Report(1.0);
            return;
        }

        var tempArchive = Path.Combine(dir, model.ArchiveFileName + ".tmp");
        var tempExtractDir = Path.Combine(dir, ".extracting");
        if (Directory.Exists(tempExtractDir))
            Directory.Delete(tempExtractDir, recursive: true);
        Directory.CreateDirectory(tempExtractDir);

        log(PluginLogLevel.Info, $"Downloading {model.DisplayName} from {model.RepositoryId}");
        await DownloadArchiveAsync(model.ArchiveUrl, tempArchive, progress, ct);

        log(PluginLogLevel.Info, $"Extracting {model.ArchiveFileName}");
        await ExtractTarGzAsync(tempArchive, tempExtractDir, ct);

        foreach (var file in Directory.EnumerateFiles(tempExtractDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tempExtractDir, file);
            var target = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }

        File.Delete(tempArchive);
        Directory.Delete(tempExtractDir, recursive: true);

        var missing = model.RequiredFiles.Where(file => !File.Exists(Path.Combine(dir, file))).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException($"Qwen3 ASR model archive is missing required files: {string.Join(", ", missing)}");

        progress?.Report(1.0);
    }

    private async Task DownloadArchiveAsync(string url, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(getHuggingFaceToken()))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", getHuggingFaceToken());

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        long readTotal = 0;
        var buffer = new byte[1024 * 128];
        var lastReport = DateTime.UtcNow;

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;

            if (total is > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
            {
                progress?.Report(Math.Min(0.9, readTotal / (double)total.Value * 0.9));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private static async Task ExtractTarGzAsync(string archivePath, string destinationDirectory, CancellationToken ct)
    {
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory)
                continue;

            var normalizedName = NormalizeTarPath(entry.Name);
            if (normalizedName is null)
                continue;

            var destinationPath = Path.Combine(destinationDirectory, normalizedName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            if (entry.DataStream is not null)
                await entry.DataStream.CopyToAsync(destination, ct);
        }
    }

    private static string? NormalizeTarPath(string name)
    {
        var normalized = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".." || Path.IsPathRooted(part)))
            return null;

        if (parts.Length > 1 && parts[0].Contains("qwen3-asr", StringComparison.OrdinalIgnoreCase))
            parts = parts[1..];

        return parts.Length == 0 ? null : Path.Combine(parts);
    }
}
