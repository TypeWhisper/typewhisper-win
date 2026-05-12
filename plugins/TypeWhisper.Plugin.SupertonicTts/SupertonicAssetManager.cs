using System.IO;
using System.Net.Http;
using System.Text;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed record SupertonicAssetFile(string RelativePath, string DownloadUrl, long EstimatedSizeBytes);

internal sealed class SupertonicAssetManager : ISupertonicAssetManager, IDisposable
{
    private const string ModelBaseUrl = "https://huggingface.co/Supertone/supertonic-3/resolve/main";
    private const string ModelSourceUrl = "https://huggingface.co/Supertone/supertonic-3";
    private const string LicenseDownloadUrl = $"{ModelBaseUrl}/LICENSE?download=true";
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<SupertonicAssetFile> _files;
    private readonly string _licenseUrl;
    private readonly bool _ownsHttpClient;

    public SupertonicAssetManager(string assetRoot)
        : this(assetRoot, new HttpClient { Timeout = TimeSpan.FromHours(1) }, DefaultFiles, LicenseDownloadUrl, ownsHttpClient: true)
    {
    }

    internal SupertonicAssetManager(
        string assetRoot,
        HttpClient httpClient,
        IReadOnlyList<SupertonicAssetFile> files,
        string licenseUrl)
        : this(assetRoot, httpClient, files, licenseUrl, ownsHttpClient: false)
    {
    }

    private SupertonicAssetManager(
        string assetRoot,
        HttpClient httpClient,
        IReadOnlyList<SupertonicAssetFile> files,
        string licenseUrl,
        bool ownsHttpClient)
    {
        AssetRoot = assetRoot;
        _httpClient = httpClient;
        _files = files;
        _licenseUrl = licenseUrl;
        _ownsHttpClient = ownsHttpClient;
    }

    public string AssetRoot { get; }

    public bool AreAssetsReady =>
        _files.All(file => File.Exists(GetPath(file.RelativePath)))
        && File.Exists(GetPath(SupertonicPaths.LicenseFileName))
        && File.Exists(GetPath(SupertonicPaths.SourceFileName));

    public async Task DownloadMissingAssetsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AssetRoot);
        var work = _files.Where(file => !File.Exists(GetPath(file.RelativePath))).ToList();
        var totalBytes = Math.Max(1, work.Sum(file => Math.Max(1, file.EstimatedSizeBytes)));
        long completedBytes = 0;

        foreach (var file in work)
        {
            var filePath = GetPath(file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var tempPath = filePath + ".tmp";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var expectedBytes = response.Content.Headers.ContentLength ?? Math.Max(1, file.EstimatedSizeBytes);
                var fileBytesRead = 0L;
                var buffer = new byte[81920];
                var lastReport = DateTime.UtcNow;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        fileBytesRead += read;

                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds >= 250)
                        {
                            progress?.Report(ClampProgress((completedBytes + Math.Min(fileBytesRead, expectedBytes)) / (double)totalBytes));
                            lastReport = now;
                        }
                    }
                }

                File.Move(tempPath, filePath, overwrite: true);
                completedBytes += Math.Max(expectedBytes, fileBytesRead);
                progress?.Report(ClampProgress(completedBytes / (double)totalBytes));
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        await WriteLicenseMetadataAsync(ct);
        progress?.Report(1.0);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task WriteLicenseMetadataAsync(CancellationToken ct)
    {
        var licensePath = GetPath(SupertonicPaths.LicenseFileName);
        if (!File.Exists(licensePath))
        {
            try
            {
                var text = await _httpClient.GetStringAsync(_licenseUrl, ct);
                await File.WriteAllTextAsync(licensePath, text, Encoding.UTF8, ct);
            }
            catch
            {
                await File.WriteAllTextAsync(
                    licensePath,
                    $"Supertonic 3 model weights are licensed under OpenRAIL-M. License source: {_licenseUrl}{Environment.NewLine}",
                    Encoding.UTF8,
                    ct);
            }
        }

        var sourceText = string.Join(Environment.NewLine,
            "Supertonic 3 model assets",
            $"Source: {ModelSourceUrl}",
            $"License: {_licenseUrl}",
            "The model weights are licensed separately under OpenRAIL-M.",
            "");
        await File.WriteAllTextAsync(GetPath(SupertonicPaths.SourceFileName), sourceText, Encoding.UTF8, ct);
    }

    private string GetPath(string relativePath) =>
        Path.Combine(AssetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static double ClampProgress(double value) =>
        Math.Max(0.0, Math.Min(1.0, value));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static IReadOnlyList<SupertonicAssetFile> DefaultFiles { get; } =
    [
        new("onnx/duration_predictor.onnx", $"{ModelBaseUrl}/onnx/duration_predictor.onnx?download=true", 4L * 1024 * 1024),
        new("onnx/text_encoder.onnx", $"{ModelBaseUrl}/onnx/text_encoder.onnx?download=true", 37L * 1024 * 1024),
        new("onnx/tts.json", $"{ModelBaseUrl}/onnx/tts.json?download=true", 16L * 1024),
        new("onnx/unicode_indexer.json", $"{ModelBaseUrl}/onnx/unicode_indexer.json?download=true", 300L * 1024),
        new("onnx/vector_estimator.onnx", $"{ModelBaseUrl}/onnx/vector_estimator.onnx?download=true", 258L * 1024 * 1024),
        new("onnx/vocoder.onnx", $"{ModelBaseUrl}/onnx/vocoder.onnx?download=true", 102L * 1024 * 1024),
        new("voice_styles/F1.json", $"{ModelBaseUrl}/voice_styles/F1.json?download=true", 300L * 1024),
        new("voice_styles/F2.json", $"{ModelBaseUrl}/voice_styles/F2.json?download=true", 300L * 1024),
        new("voice_styles/F3.json", $"{ModelBaseUrl}/voice_styles/F3.json?download=true", 300L * 1024),
        new("voice_styles/F4.json", $"{ModelBaseUrl}/voice_styles/F4.json?download=true", 300L * 1024),
        new("voice_styles/F5.json", $"{ModelBaseUrl}/voice_styles/F5.json?download=true", 300L * 1024),
        new("voice_styles/M1.json", $"{ModelBaseUrl}/voice_styles/M1.json?download=true", 300L * 1024),
        new("voice_styles/M2.json", $"{ModelBaseUrl}/voice_styles/M2.json?download=true", 300L * 1024),
        new("voice_styles/M3.json", $"{ModelBaseUrl}/voice_styles/M3.json?download=true", 300L * 1024),
        new("voice_styles/M4.json", $"{ModelBaseUrl}/voice_styles/M4.json?download=true", 300L * 1024),
        new("voice_styles/M5.json", $"{ModelBaseUrl}/voice_styles/M5.json?download=true", 300L * 1024),
    ];
}
