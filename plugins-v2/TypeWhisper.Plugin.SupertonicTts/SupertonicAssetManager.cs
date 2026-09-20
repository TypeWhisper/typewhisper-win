using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed record SupertonicAssetFile(string RelativePath, string DownloadUrl, long EstimatedSizeBytes, string? Sha256 = null);

internal sealed class SupertonicAssetManager : ISupertonicAssetManager, IDisposable
{
    private const string ModelBaseUrl = "https://huggingface.co/Supertone/supertonic-3/resolve/3cadd1ee6394adea1bd021217a0e650ede09a323";
    private const string ModelSourceUrl = "https://huggingface.co/Supertone/supertonic-3";
    private const string LicenseDownloadUrl = $"{ModelBaseUrl}/LICENSE?download=true";
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<SupertonicAssetFile> _files;
    private readonly string _licenseUrl;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Performs supertonic asset manager.
    /// </summary>
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

    /// <summary>
    /// Gets the asset root.
    /// </summary>
    public string AssetRoot { get; }

    /// <summary>
    /// Gets the are assets ready.
    /// </summary>
    public bool AreAssetsReady =>
        _files.All(IsFileReady)
        && HasContent(GetPath(SupertonicPaths.LicenseFileName))
        && HasContent(GetPath(SupertonicPaths.SourceFileName));

    /// <summary>
    /// Downloads missing assets asynchronously.
    /// </summary>
    public async Task DownloadMissingAssetsAsync(
        IProgress<double>? progress,
        string? huggingFaceToken,
        CancellationToken ct)
    {
        var normalizedToken = PluginHuggingFaceTokenHelper.NormalizeToken(huggingFaceToken);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(AssetRoot);
        progress?.Report(0);
        var work = _files.Where(file => !IsFileReady(file)).ToList();
        var totalBytes = Math.Max(1, work.Sum(file => Math.Max(1, file.EstimatedSizeBytes)));
        long completedBytes = 0;

        foreach (var file in work)
        {
            var filePath = GetPath(file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var tempPath = filePath + ".tmp";
            var completedFile = false;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
                ApplyHuggingFaceAuthorization(request, normalizedToken);
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

                ct.ThrowIfCancellationRequested();
                if (response.Content.Headers.ContentLength is long declaredLength && fileBytesRead != declaredLength)
                    throw new InvalidDataException("The model download is incomplete. Retry downloading.");
                if (file.Sha256 is not null)
                {
                    await using var downloaded = File.OpenRead(tempPath);
                    var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct));
                    if (fileBytesRead != file.EstimatedSizeBytes || !actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Model asset verification failed. Retry downloading.");
                }
                ct.ThrowIfCancellationRequested();
                File.Move(tempPath, filePath, overwrite: true);
                completedFile = true;
                completedBytes += Math.Max(expectedBytes, fileBytesRead);
                progress?.Report(ClampProgress(completedBytes / (double)totalBytes));
            }
            finally
            {
                if (!completedFile)
                    TryDelete(tempPath);
            }
        }

        await WriteLicenseMetadataAsync(normalizedToken, ct);
        progress?.Report(1.0);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task WriteLicenseMetadataAsync(string? huggingFaceToken, CancellationToken ct)
    {
        var licensePath = GetPath(SupertonicPaths.LicenseFileName);
        if (!File.Exists(licensePath))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _licenseUrl);
                ApplyHuggingFaceAuthorization(request, huggingFaceToken);
                using var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(ct);
                await WriteMetadataAsync(licensePath, text, ct);
            }
            catch (HttpRequestException)
            {
                await WriteFallbackLicenseAsync(licensePath, ct);
            }
            catch (IOException)
            {
                await WriteFallbackLicenseAsync(licensePath, ct);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteFallbackLicenseAsync(licensePath, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await WriteFallbackLicenseAsync(licensePath, ct);
            }
        }

        var sourceText = string.Join(Environment.NewLine,
            "Supertonic 3 model assets",
            $"Source: {ModelSourceUrl}",
            $"License: {_licenseUrl}",
            "The model weights are licensed separately under OpenRAIL-M.",
            "");
        await WriteMetadataAsync(GetPath(SupertonicPaths.SourceFileName), sourceText, ct);
    }

    private Task WriteFallbackLicenseAsync(string licensePath, CancellationToken ct) =>
        WriteMetadataAsync(
            licensePath,
            $"Supertonic 3 model weights are licensed under OpenRAIL-M. License source: {_licenseUrl}{Environment.NewLine}",
            ct);

    private static bool HasContent(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };

    private static async Task WriteMetadataAsync(string path, string text, CancellationToken ct)
    {
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text, Encoding.UTF8, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    private bool IsFileReady(SupertonicAssetFile file)
    {
        var info = new FileInfo(GetPath(file.RelativePath));
        return info.Exists && info.Length > 0 && (file.Sha256 is null || info.Length == file.EstimatedSizeBytes);
    }

    private string GetPath(string relativePath) =>
        Path.Combine(AssetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static double ClampProgress(double value) =>
        Math.Max(0.0, Math.Min(0.99, value));

    private static void ApplyHuggingFaceAuthorization(
        HttpRequestMessage request,
        string? huggingFaceToken)
    {
        if (huggingFaceToken is not null
            && string.Equals(request.RequestUri?.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", huggingFaceToken);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            TraceDeleteFailure(path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TraceDeleteFailure(path, ex);
        }
        catch (NotSupportedException ex)
        {
            TraceDeleteFailure(path, ex);
        }
        catch (System.Security.SecurityException ex)
        {
            TraceDeleteFailure(path, ex);
        }
    }

    private static void TraceDeleteFailure(string path, Exception ex) =>
        Trace.TraceWarning($"Failed to delete Supertonic temporary asset '{path}': {ex.Message}");

    private static IReadOnlyList<SupertonicAssetFile> DefaultFiles { get; } =
    [
        new("onnx/duration_predictor.onnx", $"{ModelBaseUrl}/onnx/duration_predictor.onnx?download=true", 3700147L, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        new("onnx/text_encoder.onnx", $"{ModelBaseUrl}/onnx/text_encoder.onnx?download=true", 36416150L, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        new("onnx/tts.json", $"{ModelBaseUrl}/onnx/tts.json?download=true", 8253L, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
        new("onnx/unicode_indexer.json", $"{ModelBaseUrl}/onnx/unicode_indexer.json?download=true", 277676L, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
        new("onnx/vector_estimator.onnx", $"{ModelBaseUrl}/onnx/vector_estimator.onnx?download=true", 256534781L, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
        new("onnx/vocoder.onnx", $"{ModelBaseUrl}/onnx/vocoder.onnx?download=true", 101424195L, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
        new("voice_styles/F1.json", $"{ModelBaseUrl}/voice_styles/F1.json?download=true", 292046L, "bbdec6ee00231c2c742ad05483df5334cab3b52fda3ba38e6a07059c4563dbc2"),
        new("voice_styles/F2.json", $"{ModelBaseUrl}/voice_styles/F2.json?download=true", 292423L, "7c722c6a72707b1a77f035d67f0d1351ba187738e06f7683e8c72b1df3477fc6"),
        new("voice_styles/F3.json", $"{ModelBaseUrl}/voice_styles/F3.json?download=true", 290794L, "12f6ef2573baa2defa1128069cb59f203e3ab67c92af77b42df8a0e3a2f7c6ab"),
        new("voice_styles/F4.json", $"{ModelBaseUrl}/voice_styles/F4.json?download=true", 291808L, "c2fa764c1225a76dfc3e2c73e8aa4f70d9ee48793860eb34c295fff01c2e032b"),
        new("voice_styles/F5.json", $"{ModelBaseUrl}/voice_styles/F5.json?download=true", 291479L, "45966e73316415626cf41a7d1c6f3b4c70dbc1ba2bee5c1978ef0ce33244fc8d"),
        new("voice_styles/M1.json", $"{ModelBaseUrl}/voice_styles/M1.json?download=true", 291748L, "e35604687f5d23694b8e91593a93eec0e4eca6c0b02bb8ed69139ab2ea6b0a5b"),
        new("voice_styles/M2.json", $"{ModelBaseUrl}/voice_styles/M2.json?download=true", 292055L, "b76cbf62bac707c710cf0ae5aba5e31eea1a6339a9734bfae33ab98499534a50"),
        new("voice_styles/M3.json", $"{ModelBaseUrl}/voice_styles/M3.json?download=true", 290198L, "ea1ac35ccb91b0d7ecad533a2fbd0eec10c91513d8951e3b25fbba99954e159b"),
        new("voice_styles/M4.json", $"{ModelBaseUrl}/voice_styles/M4.json?download=true", 291522L, "ca8eefad4fcd989c9379032ff3e50738adc547eeb5e221b82593a6d7b3bac303"),
        new("voice_styles/M5.json", $"{ModelBaseUrl}/voice_styles/M5.json?download=true", 291469L, "dd22b92740314321f8ae11c5e87f8dd60d060f15dd3a632b5adf77f471f77af2"),
    ];
}
