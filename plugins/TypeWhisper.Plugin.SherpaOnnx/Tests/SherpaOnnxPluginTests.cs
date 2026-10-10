using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx.Tests;

public class SherpaOnnxPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new SherpaOnnxPlugin();

        Assert.NotNull(manifest);
        Assert.Equal("1.3.0", manifest.Version);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task RemoveModelAsync_DeletesOnlyTheRequestedModelDirectory()
    {
        var tempDirectory = Path.Join(Path.GetTempPath(), $"tw-sherpa-remove-{Guid.NewGuid():N}");
        try
        {
            var sut = new SherpaOnnxPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(tempDirectory));
            var requestedDirectory = Path.Join(tempDirectory, "Models", "parakeet-tdt-0.6b");
            var otherDirectory = Path.Join(tempDirectory, "Models", "canary-180m-flash");
            Directory.CreateDirectory(requestedDirectory);
            Directory.CreateDirectory(otherDirectory);
            await File.WriteAllTextAsync(Path.Join(requestedDirectory, "partial.tmp"), "partial");
            await File.WriteAllTextAsync(Path.Join(otherDirectory, "keep.txt"), "keep");

            await sut.RemoveModelAsync("parakeet-tdt-0.6b", CancellationToken.None);

            Assert.True(sut.SupportsModelRemoval);
            Assert.False(Directory.Exists(requestedDirectory));
            Assert.True(Directory.Exists(otherDirectory));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_StopsWhenMissingFilesDoNotFitAfterRemovingAbandonedPartials()
    {
        var tempDirectory = Path.Join(Path.GetTempPath(), $"tw-sherpa-space-{Guid.NewGuid():N}");
        try
        {
            var sut = new SherpaOnnxPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(tempDirectory));
            var modelDirectory = Path.Join(tempDirectory, "Models", "parakeet-tdt-0.6b");
            Directory.CreateDirectory(modelDirectory);
            await File.WriteAllTextAsync(Path.Join(modelDirectory, "tokens.txt"), "tokens");
            // A partial download and a complete one that was never verified; neither may count or stay.
            var abandoned = Path.Join(modelDirectory, "encoder.int8.onnx.tmp");
            var unverified = Path.Join(modelDirectory, "decoder.int8.onnx.unverified");
            await File.WriteAllBytesAsync(abandoned, new byte[32]);
            await File.WriteAllBytesAsync(unverified, new byte[32]);
            sut.AvailableBytes = directory =>
            {
                Assert.Equal(modelDirectory, directory);
                Assert.False(File.Exists(abandoned));
                Assert.False(File.Exists(unverified));
                return 100L * 1024 * 1024;
            };

            var error = await Assert.ThrowsAsync<TypeWhisper.PluginSDK.Helpers.InsufficientModelStorageException>(
                () => sut.DownloadModelAsync("parakeet-tdt-0.6b", null, CancellationToken.None));

            // tokens.txt is already present, so only the encoder, decoder and joiner still need space.
            Assert.Equal((652L + 12 + 6) * 1024 * 1024 + TypeWhisper.PluginSDK.Helpers.ModelStorageSpace.ReserveBytes, error.RequiredBytes);
            Assert.StartsWith("Not enough free disk space to download Parakeet TDT 0.6B.", error.Message);
            Assert.Equal(["tokens.txt"], Directory.GetFiles(modelDirectory).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void TranscriptionModels_RecommendParakeetUltraAndKeepTheNvidiaModels()
    {
        var models = new SherpaOnnxPlugin().TranscriptionModels;

        Assert.Equal(["parakeet-ultra-0.6b", "parakeet-tdt-0.6b", "canary-180m-flash"], models.Select(model => model.Id));
        Assert.Equal("parakeet-ultra-0.6b", Assert.Single(models, model => model.IsRecommended).Id);
        Assert.Equal(["Moondream", "NVIDIA", "NVIDIA"], models.Select(model => model.Publisher));
        Assert.Equal(models[1].LanguageCodes, models[0].LanguageCodes);
    }

    [Fact]
    public async Task VerifyChecksumAsync_DeletesFilesThatDoNotMatchThePinnedHash()
    {
        var path = Path.Join(Path.GetTempPath(), $"tw-sherpa-checksum-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "model");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("model"u8)).ToLowerInvariant();

            await SherpaOnnxPlugin.VerifyChecksumAsync("encoder.int8.onnx", expected, path, CancellationToken.None);
            Assert.True(File.Exists(path));

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => SherpaOnnxPlugin.VerifyChecksumAsync("encoder.int8.onnx", new string('0', 64), path, CancellationToken.None));
            Assert.Contains("encoder.int8.onnx", error.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultModels_PinEveryFileToARevisionAndSha256()
    {
        // Hugging Face files name a commit; main would let the bytes change under the same URL.
        var pinnedUrl = new Regex(
            @"^https://(huggingface\.co/[^/]+/[^/]+/resolve/[0-9a-f]{40}|github\.com/TypeWhisper/typewhisper-win/releases/download/[^/]+)/[^/]+$");
        var files = SherpaOnnxPlugin.DefaultModels.SelectMany(model => model.Files).ToList();

        Assert.Equal(11, files.Count);
        Assert.All(files, file => Assert.Matches(pinnedUrl, file.DownloadUrl));
        Assert.All(files, file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256));
        Assert.All(
            SherpaOnnxPlugin.DefaultModels.Single(model => model.Id == "parakeet-tdt-0.6b").Files,
            file => Assert.StartsWith(
                "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78/",
                file.DownloadUrl));
        Assert.All(
            SherpaOnnxPlugin.DefaultModels.Single(model => model.Id == "canary-180m-flash").Files,
            file => Assert.StartsWith(
                "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8/resolve/9077164e0d3dd1d5353743e89ceaa1d3a770838c/",
                file.DownloadUrl));
    }

    [Fact]
    public async Task DownloadModelAsync_KeepsDownloadsThatMatchThePinnedHashes()
    {
        var tempDirectory = Path.Join(Path.GetTempPath(), $"tw-sherpa-download-{Guid.NewGuid():N}");
        try
        {
            var encoder = Encoding.ASCII.GetBytes("encoder");
            var tokens = Encoding.ASCII.GetBytes("tokens");
            var handler = new ModelDownloadHandler(url => url switch
            {
                TestEncoderUrl => encoder,
                TestTokensUrl => tokens,
                _ => null
            });
            using var client = new HttpClient(handler);
            var sut = new SherpaOnnxPlugin(client, [CreateTestModel(encoder, tokens)]);
            await sut.ActivateAsync(new FakePluginHostServices(tempDirectory));
            sut.AvailableBytes = _ => long.MaxValue;
            var modelDirectory = Path.Join(tempDirectory, "Models", "test-model");

            await sut.DownloadModelAsync("test-model", null, CancellationToken.None);

            Assert.True(sut.IsModelDownloaded("test-model"));
            Assert.Equal([TestEncoderUrl, TestTokensUrl], handler.RequestedUrls);
            Assert.Equal("encoder", await File.ReadAllTextAsync(Path.Join(modelDirectory, "encoder.int8.onnx")));
            Assert.Equal("tokens", await File.ReadAllTextAsync(Path.Join(modelDirectory, "tokens.txt")));
            // No partial or unverified copies remain next to the published files.
            Assert.Equal(["encoder.int8.onnx", "tokens.txt"], ListModelFiles(modelDirectory));

            // A complete model is not requested again.
            await sut.DownloadModelAsync("test-model", null, CancellationToken.None);
            Assert.Equal(2, handler.RequestedUrls.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_RemovesDownloadsThatDoNotMatchThePinnedHash()
    {
        var tempDirectory = Path.Join(Path.GetTempPath(), $"tw-sherpa-download-{Guid.NewGuid():N}");
        try
        {
            // Served for every URL: whatever it is, it is not the pinned Parakeet encoder.
            var handler = new ModelDownloadHandler(_ => Encoding.ASCII.GetBytes("not the model"));
            using var client = new HttpClient(handler);
            var sut = new SherpaOnnxPlugin(client);
            await sut.ActivateAsync(new FakePluginHostServices(tempDirectory));
            sut.AvailableBytes = _ => long.MaxValue;

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => sut.DownloadModelAsync("parakeet-tdt-0.6b", null, CancellationToken.None));

            Assert.Equal("The downloaded encoder.int8.onnx does not match its expected checksum.", error.Message);
            Assert.Equal(
                ["https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78/encoder.int8.onnx"],
                handler.RequestedUrls);
            Assert.False(sut.IsModelDownloaded("parakeet-tdt-0.6b"));
            // The rejected download is gone in every form, and the remaining files were never requested.
            Assert.Empty(ListModelFiles(Path.Join(tempDirectory, "Models", "parakeet-tdt-0.6b")));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_KeepsExistingFilesWithoutDownloadingThemAgain()
    {
        var tempDirectory = Path.Join(Path.GetTempPath(), $"tw-sherpa-download-{Guid.NewGuid():N}");
        try
        {
            // Files from an earlier plugin version carry no hash and must stay usable as they are.
            CreateParakeetModelFiles(tempDirectory);
            var handler = new ModelDownloadHandler(_ => null);
            using var client = new HttpClient(handler);
            var sut = new SherpaOnnxPlugin(client);
            await sut.ActivateAsync(new FakePluginHostServices(tempDirectory));
            // With every file present nothing needs space, so the check must not even run.
            sut.AvailableBytes = _ => 0;
            var modelDirectory = Path.Join(tempDirectory, "Models", "parakeet-tdt-0.6b");

            await sut.DownloadModelAsync("parakeet-tdt-0.6b", null, CancellationToken.None);

            Assert.Empty(handler.RequestedUrls);
            Assert.True(sut.IsModelDownloaded("parakeet-tdt-0.6b"));
            Assert.Equal(["decoder.int8.onnx", "encoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"], ListModelFiles(modelDirectory));
            Assert.All(Directory.GetFiles(modelDirectory), path => Assert.Equal("test", File.ReadAllText(path)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_DownloadsOnlyTheMissingFilesOfAnExistingInstall()
    {
        var tempDirectory = Path.Join(Path.GetTempPath(), $"tw-sherpa-download-{Guid.NewGuid():N}");
        try
        {
            var encoder = Encoding.ASCII.GetBytes("encoder");
            var tokens = Encoding.ASCII.GetBytes("tokens");
            var handler = new ModelDownloadHandler(url => url == TestTokensUrl ? tokens : null);
            using var client = new HttpClient(handler);
            var sut = new SherpaOnnxPlugin(client, [CreateTestModel(encoder, tokens)]);
            await sut.ActivateAsync(new FakePluginHostServices(tempDirectory));
            sut.AvailableBytes = _ => long.MaxValue;
            var modelDirectory = Path.Join(tempDirectory, "Models", "test-model");
            Directory.CreateDirectory(modelDirectory);
            // Downloaded by an earlier plugin version: not the pinned bytes, and not touched.
            await File.WriteAllTextAsync(Path.Join(modelDirectory, "encoder.int8.onnx"), "legacy encoder");

            await sut.DownloadModelAsync("test-model", null, CancellationToken.None);

            Assert.Equal([TestTokensUrl], handler.RequestedUrls);
            Assert.Equal("legacy encoder", await File.ReadAllTextAsync(Path.Join(modelDirectory, "encoder.int8.onnx")));
            Assert.Equal("tokens", await File.ReadAllTextAsync(Path.Join(modelDirectory, "tokens.txt")));
            Assert.True(sut.IsModelDownloaded("test-model"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateRecognizerConfigs_RunOnTheCpu()
    {
        var modelDir = Path.Join(Path.GetTempPath(), "tw-sherpa-test");

        var parakeet = SherpaOnnxPlugin.CreateParakeetConfig(modelDir);
        var canary = SherpaOnnxPlugin.CreateCanaryConfig(modelDir, "en", "en");

        Assert.Equal("cpu", parakeet.ModelConfig.Provider);
        Assert.Equal("cpu", canary.ModelConfig.Provider);
    }

    [Fact]
    public void NativeRuntime_ResolvesCpuRuntimeFromPluginAssemblyDirectory()
    {
        var pluginAssembly = Path.Join("C:", "TypeWhisper", "Plugins", "com.typewhisper.sherpa-onnx", "TypeWhisper.Plugin.SherpaOnnx.dll");

        var runtimeDirectory = SherpaOnnxNativeRuntime.ResolveBundledRuntimeDirectory(
            pluginAssembly,
            Architecture.X64);

        Assert.Equal(
            Path.Join("C:", "TypeWhisper", "Plugins", "com.typewhisper.sherpa-onnx", "runtimes", "win-x64", "native"),
            runtimeDirectory);
    }

    [Fact]
    public void NativeRuntime_MissingRuntimeMessageNamesExpectedPluginDirectory()
    {
        var runtimeDirectory = Path.Join("C:", "TypeWhisper", "Plugins", "com.typewhisper.sherpa-onnx", "runtimes", "win-x64", "native");

        var message = SherpaOnnxNativeRuntime.CreateMissingRuntimeMessage(runtimeDirectory);

        Assert.Contains("sherpa-onnx-c-api.dll", message);
        Assert.Contains("sherpaort.dll", message);
        Assert.Contains(runtimeDirectory, message);
        Assert.DoesNotContain("AppContext.BaseDirectory", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WinUIAppProject_DoesNotReferenceSherpaOnnxPackage()
    {
        var repoRoot = GetRepoRoot();
        var projectPath = Path.Join(repoRoot, "src", "TypeWhisper.WinUI", "TypeWhisper.WinUI.csproj");

        var project = File.ReadAllText(projectPath);

        Assert.DoesNotContain("org.k2fsa.sherpa.onnx", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SherpaPluginProject_CopiesWindowsNativeRuntimeDirectoriesToBundledPluginOutput()
    {
        var repoRoot = GetRepoRoot();
        var projectPath = Path.Join(
            repoRoot,
            "plugins",
            "TypeWhisper.Plugin.SherpaOnnx",
            "TypeWhisper.Plugin.SherpaOnnx.csproj");

        var project = File.ReadAllText(projectPath);

        Assert.DoesNotContain("<ExcludeAssets>runtime</ExcludeAssets>", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"$(TargetDir)runtimes\win-x64\**\*.*", project);
        Assert.Contains(@"$(TargetDir)runtimes\win-arm64\**\*.*", project);
        Assert.Contains(@"$(TargetDir)runtimes\win-x86\**\*.*", project);
        Assert.Contains("PatchSherpaOnnxRuntimeImport", project);
        Assert.Contains("sherpaort.dll", project);
        Assert.Contains(@"$(PluginOutputDir)runtimes\win-x64\native\sherpa-onnx-c-api.dll", project);
        Assert.Contains(@"$(PluginOutputDir)runtimes\win-x64\native\onnxruntime.dll", project);
        Assert.Contains(@"$(PluginOutputDir)runtimes\win-x64\native\sherpaort.dll", project);
        Assert.Contains(@"$(PluginOutputDir)runtimes\win-x86\native\sherpa-onnx-c-api.dll", project);
        Assert.Contains(@"$(PluginOutputDir)runtimes\win-x86\native\onnxruntime.dll", project);
        Assert.Contains(@"$(PluginOutputDir)runtimes\win-x86\native\sherpaort.dll", project);
    }

    [Fact]
    public void CanaryChunks_KeepShortRecordingsWhole()
    {
        var samples = new float[SherpaOnnxPlugin.CanaryChunkSeconds * SherpaOnnxPlugin.SampleRate];

        Assert.Equal([(0, samples.Length)], SherpaOnnxPlugin.CreateCanaryChunks(samples));
    }

    [Fact]
    public void CanaryChunks_CutLongRecordingsInsidePauses()
    {
        const int rate = SherpaOnnxPlugin.SampleRate;
        // One minute of tone with 300 ms pauses at 13 s, 22 s, 37 s and 55 s.
        var samples = Enumerable.Range(0, 60 * rate).Select(i => 0.5f * MathF.Sin(i * 0.1f)).ToArray();
        int[] pauses = [13 * rate, 22 * rate, 37 * rate, 55 * rate];
        foreach (var pause in pauses)
            Array.Clear(samples, pause, 3 * rate / 10);

        var chunks = SherpaOnnxPlugin.CreateCanaryChunks(samples);

        Assert.Equal(0, chunks[0].Offset);
        Assert.Equal(samples.Length, chunks[^1].Offset + chunks[^1].Count);
        for (var index = 1; index < chunks.Count; index++)
        {
            Assert.Equal(chunks[index - 1].Offset + chunks[index - 1].Count, chunks[index].Offset);
            Assert.Contains(pauses, pause => chunks[index].Offset > pause && chunks[index].Offset < pause + 3 * rate / 10);
        }
        Assert.All(chunks, chunk => Assert.InRange(
            chunk.Count,
            rate,
            (SherpaOnnxPlugin.CanaryChunkSeconds + SherpaOnnxPlugin.CanaryChunkSearchSeconds) * rate));
    }

    [Fact]
    public void CanaryChunks_LeaveAtLeastOneSecondAfterTheLastCut()
    {
        const int rate = SherpaOnnxPlugin.SampleRate;
        var samples = Enumerable.Range(0, SherpaOnnxPlugin.CanaryChunkSeconds * rate + rate / 2)
            .Select(i => 0.5f * MathF.Sin(i * 0.1f)).ToArray();
        // Trailing silence would attract the cut; the final chunk must still be usable.
        Array.Clear(samples, samples.Length - rate / 4, rate / 4);

        var chunks = SherpaOnnxPlugin.CreateCanaryChunks(samples);

        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[1].Count >= rate);
    }

    private static void CreateParakeetModelFiles(string pluginDataDirectory)
    {
        var modelDir = Path.Join(pluginDataDirectory, "Models", "parakeet-tdt-0.6b");
        Directory.CreateDirectory(modelDir);
        foreach (var fileName in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
            File.WriteAllText(Path.Join(modelDir, fileName), "test");
    }

    private static string GetRepoRoot() =>
        Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    private const string TestEncoderUrl = "https://example.test/models/test-model/encoder.int8.onnx";
    private const string TestTokensUrl = "https://example.test/models/test-model/tokens.txt";

    // A two-file model whose pins are the hashes of the payloads the fake server will return.
    private static SherpaOnnxPlugin.ModelDefinition CreateTestModel(byte[] encoder, byte[] tokens) => new(
        "test-model", "Test Model", "Test", "~1 MB", 1, 1, false, false,
        [
            new SherpaOnnxPlugin.ModelFileDefinition("encoder.int8.onnx", TestEncoderUrl, 1, Sha256Hex(encoder)),
            new SherpaOnnxPlugin.ModelFileDefinition("tokens.txt", TestTokensUrl, 1, Sha256Hex(tokens))
        ]);

    private static IEnumerable<string> ListModelFiles(string modelDirectory) =>
        Directory.EnumerateFileSystemEntries(modelDirectory)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ModelDownloadHandler(Func<string, byte[]?> files) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var body = files(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class FakePluginHostServices(string pluginDataDirectory) : IPluginHostServices
    {
        public string PluginDataDirectory { get; } = pluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new NoOpPluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class NoOpPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoOpPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }
}
