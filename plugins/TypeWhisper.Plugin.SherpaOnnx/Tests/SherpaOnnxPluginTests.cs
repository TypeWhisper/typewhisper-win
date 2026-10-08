using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Common;
using SharpCompress.Writers;
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
        Assert.Equal("1.2.1", manifest.Version);
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

    [Theory]
    [InlineData(TranscriptionAccelerationPreference.Auto, false, "cpu")]
    [InlineData(TranscriptionAccelerationPreference.Auto, true, "cuda")]
    [InlineData(TranscriptionAccelerationPreference.Cpu, false, "cpu")]
    [InlineData(TranscriptionAccelerationPreference.NvidiaCuda, false, "cuda")]
    public void GetProvider_MapsAccelerationPreference(
        TranscriptionAccelerationPreference preference,
        bool cudaRuntimeInstalled,
        string expectedProvider)
    {
        var provider = SherpaOnnxPlugin.GetProvider(preference, cudaRuntimeInstalled);

        Assert.Equal(expectedProvider, provider);
    }

    [CudaPlatformFact(supported: false)]
    public async Task ResolveProviderForLoadAsync_UnsupportedCudaPlatformRejectsBeforeInstallation()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: false);
        using var sut = new SherpaOnnxPlugin(installer);
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveProviderForLoadAsync(CancellationToken.None));

        Assert.Contains("only available on Windows x64", error.Message);
        Assert.False(installer.EnsureInstalledCalled);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task ResolveProviderForLoadAsync_AutoUsesCpuWithoutInstallingCudaRuntime()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: false);
        var sut = new SherpaOnnxPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);

        var provider = await sut.ResolveProviderForLoadAsync(CancellationToken.None);

        Assert.Equal("cpu", provider);
        Assert.False(installer.EnsureInstalledCalled);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Contains("CUDA runtime is not installed", sut.AccelerationStatus.Detail);
    }

    [CudaPlatformFact]
    public async Task ResolveProviderForLoadAsync_ExplicitCudaInstallsRuntimeAndUsesCuda()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: false);
        var sut = new SherpaOnnxPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var provider = await sut.ResolveProviderForLoadAsync(CancellationToken.None);

        Assert.Equal("cuda", provider);
        Assert.True(installer.EnsureInstalledCalled);
        Assert.Equal(TranscriptionAccelerationBackend.NvidiaCuda, sut.AccelerationStatus.ActiveBackend);
        Assert.Equal("Using CUDA", sut.AccelerationStatus.DisplayText);
    }

    [CudaPlatformFact]
    public async Task ResolveProviderForLoadAsync_ExplicitCudaInstallFailureSetsUnavailableStatus()
    {
        var installer = new FakeCudaRuntimeInstaller(
            isInstalled: false,
            installException: new InvalidOperationException("download blocked"));
        var sut = new SherpaOnnxPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveProviderForLoadAsync(CancellationToken.None));

        Assert.Contains("download blocked", ex.Message);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
        Assert.Contains("download blocked", sut.AccelerationStatus.Detail);
    }

    [CudaPlatformFact]
    public async Task LoadModelAsync_ExplicitCudaProviderFailureSetsUnavailableStatus()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-load-{Guid.NewGuid():N}");
        try
        {
            var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
            var sut = new SherpaOnnxPlugin(
                installer,
                (_, _, _) => throw new InvalidOperationException("CUDA provider failed to initialize."),
                new FakeCudaRuntimeProbe(new CudaRuntimeProbeResult(true, null)));
            var host = new FakePluginHostServices(tempDir);
            CreateParakeetModelFiles(tempDir);

            await sut.ActivateAsync(host);
            sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None));

            Assert.Contains("CUDA provider failed to initialize", ex.Message);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
            Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
            Assert.Contains("CUDA provider failed to initialize", sut.AccelerationStatus.Detail);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [CudaPlatformFact]
    public async Task LoadModelAsync_ExplicitCudaProbeFailure_DoesNotLoadNativeRuntimeInHostProcess()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-probe-{Guid.NewGuid():N}");
        try
        {
            var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
            var probe = new FakeCudaRuntimeProbe(
                new CudaRuntimeProbeResult(false, "Native probe exited with code 0xC0000409."));
            var recognizerFactoryCalled = false;
            var sut = new SherpaOnnxPlugin(
                installer,
                (_, _, _) =>
                {
                    recognizerFactoryCalled = true;
                    throw new InvalidOperationException("Recognizer must not be created after probe failure.");
                },
                probe);
            var host = new FakePluginHostServices(tempDir);
            CreateParakeetModelFiles(tempDir);

            await sut.ActivateAsync(host);
            sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None));

            Assert.Contains("0xC0000409", ex.Message);
            Assert.False(recognizerFactoryCalled);
            Assert.Equal(1, probe.CallCount);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
            Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadModelAsync_AutoCudaProbeFailure_PreservesUnavailableStatusAfterCpuFallback()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-probe-fallback-{Guid.NewGuid():N}");
        try
        {
            var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
            var probe = new FakeCudaRuntimeProbe(
                new CudaRuntimeProbeResult(false, "Native probe exited with code 0xC0000409."));
            string? loadedProvider = null;
            var sut = new SherpaOnnxPlugin(
                installer,
                (_, _, provider) =>
                {
                    loadedProvider = provider;
                    return null!;
                },
                probe);
            var host = new FakePluginHostServices(tempDir);
            CreateParakeetModelFiles(tempDir);

            await sut.ActivateAsync(host);
            sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);

            await sut.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None);

            Assert.Equal("cpu", loadedProvider);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
            Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
            Assert.Contains("0xC0000409", sut.AccelerationStatus.Detail);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CudaProbeCache_InvalidatesWhenTheNativeRuntimeChanges()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-probe-cache-{Guid.NewGuid():N}");
        var pluginDir = Path.Join(tempDir, "plugin");
        var modelDir = Path.Join(tempDir, "model");
        var runtimeDir = Path.Join(tempDir, "runtime");
        var executablePath = Path.Join(tempDir, "TypeWhisper.exe");
        var cachePath = Path.Join(runtimeDir, ".typewhisper-cuda-probe-success");
        try
        {
            Directory.CreateDirectory(pluginDir);
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(runtimeDir);
            File.WriteAllText(executablePath, "host");
            File.WriteAllText(Path.Join(pluginDir, "TypeWhisper.Plugin.SherpaOnnx.dll"), "plugin");
            File.WriteAllText(Path.Join(modelDir, "encoder.onnx"), "model");
            var runtimeDll = Path.Join(runtimeDir, "onnxruntime.dll");
            File.WriteAllText(runtimeDll, "runtime-v1");

            var first = SherpaCudaRuntimeProbe.BuildCacheFingerprint(
                "parakeet-tdt-0.6b",
                executablePath,
                pluginDir,
                modelDir,
                runtimeDir);
            SherpaCudaRuntimeProbe.RecordCachedSuccess(cachePath, first);

            Assert.True(SherpaCudaRuntimeProbe.IsCachedSuccess(cachePath, first));

            File.AppendAllText(runtimeDll, "-changed");
            var second = SherpaCudaRuntimeProbe.BuildCacheFingerprint(
                "parakeet-tdt-0.6b",
                executablePath,
                pluginDir,
                modelDir,
                runtimeDir);

            Assert.NotEqual(first, second);
            Assert.False(SherpaCudaRuntimeProbe.IsCachedSuccess(cachePath, second));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadModelAsync_AutoCudaAndCpuFailureReportsCpuFallbackFailure()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-load-{Guid.NewGuid():N}");
        try
        {
            var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
            var sut = new SherpaOnnxPlugin(
                installer,
                (_, _, provider) => throw new InvalidOperationException(
                    $"{provider} provider failed to initialize."),
                new FakeCudaRuntimeProbe(new CudaRuntimeProbeResult(true, null)));
            var host = new FakePluginHostServices(tempDir);
            CreateParakeetModelFiles(tempDir);

            await sut.ActivateAsync(host);
            sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None));

            Assert.Contains("cpu provider failed to initialize", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
            Assert.Equal("Native runtime unavailable", sut.AccelerationStatus.DisplayText);
            Assert.Contains("cpu provider failed to initialize", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CUDA unavailable", sut.AccelerationStatus.DisplayText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [CudaPlatformFact]
    public async Task ResolveProviderForLoadAsync_BackendSwitchAfterNativeLoadRequiresRestart()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
        var sut = new SherpaOnnxPlugin(installer);

        sut.MarkNativeRuntimeLoadedForTests("cpu");
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveProviderForLoadAsync(CancellationToken.None));

        Assert.Contains("restart", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sut.AccelerationStatus.RequiresRestart);
        Assert.Contains("restart", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("cuda")]
    public void CreateRecognizerConfigs_UseMappedProvider(string provider)
    {
        var modelDir = Path.Join(Path.GetTempPath(), "tw-sherpa-test");

        var parakeet = SherpaOnnxPlugin.CreateParakeetConfig(modelDir, provider);
        var canary = SherpaOnnxPlugin.CreateCanaryConfig(modelDir, "en", "en", provider);

        Assert.Equal(provider, parakeet.ModelConfig.Provider);
        Assert.Equal(provider, canary.ModelConfig.Provider);
    }

    [Fact]
    public void CudaRuntimeInstaller_IsNotInstalledWhenOnnxCudaDependenciesAreMissing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            var nativeDir = Path.Join(
                tempDir,
                "Runtimes",
                "sherpa-onnx-cuda",
                SherpaCudaRuntimeInstaller.RuntimeVersion,
                "native");
            Directory.CreateDirectory(nativeDir);
            File.WriteAllText(Path.Join(nativeDir, "sherpa-onnx-c-api.dll"), "");
            File.WriteAllText(Path.Join(nativeDir, "onnxruntime.dll"), "");
            File.WriteAllText(Path.Join(nativeDir, "sherpaort.dll"), "");
            File.WriteAllText(Path.Join(nativeDir, "onnxruntime_providers_cuda.dll"), "");

            var installer = new SherpaCudaRuntimeInstaller(tempDir, new HttpClient());

            Assert.False(installer.IsInstalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CudaRuntimeInstaller_CopiesAndPatchesSherpaRuntimeAlias()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var sherpaNative = Path.Join(tempDir, "sherpa-onnx-c-api.dll");
            File.WriteAllBytes(
                sherpaNative,
                Encoding.ASCII.GetBytes("prefix onnxruntime.dll\0 suffix"));
            File.WriteAllText(Path.Join(tempDir, "onnxruntime.dll"), "runtime");

            SherpaCudaRuntimeInstaller.EnsureSherpaRuntimeImportAlias(tempDir);

            var patchedNative = Encoding.ASCII.GetString(File.ReadAllBytes(sherpaNative));
            Assert.True(File.Exists(Path.Join(tempDir, "sherpaort.dll")));
            Assert.DoesNotContain("onnxruntime.dll", patchedNative);
            Assert.Contains("sherpaort.dll", patchedNative);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CudaRuntimeInstaller_InstallsDownloadsThatMatchThePinnedHashes()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            var archive = CreateSherpaCudaArchive();
            var wheel = CreateCublasWheel();
            var handler = new PinnedDownloadHandler(archive, wheel);
            using var client = new HttpClient(handler);
            var installer = new SherpaCudaRuntimeInstaller(tempDir, client, CreateCudaRuntimePackage(archive, wheel));

            Assert.False(installer.IsInstalled);
            await installer.EnsureInstalledAsync(CancellationToken.None);

            Assert.True(installer.IsInstalled);
            Assert.Equal([TestArchiveUrl, TestWheelUrl], handler.RequestedUrls);
            Assert.Equal("cublas", File.ReadAllText(Path.Join(installer.RuntimeDirectory, "cublas64_12.dll")));
            Assert.Equal("onnxruntime", File.ReadAllText(Path.Join(installer.RuntimeDirectory, "sherpaort.dll")));
            Assert.False(File.Exists(Path.Join(installer.RuntimeDirectory, "RECORD")));
            Assert.Equal(["installed.json", "native"], ListRuntimeRootEntries(tempDir));

            // A verified installation is not downloaded again.
            await installer.EnsureInstalledAsync(CancellationToken.None);
            Assert.Equal(2, handler.RequestedUrls.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CudaRuntimeInstaller_RejectsArchiveThatDoesNotMatchThePinnedHash()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            var archive = CreateSherpaCudaArchive();
            var wheel = CreateCublasWheel();
            var handler = new PinnedDownloadHandler(archive, wheel);
            using var client = new HttpClient(handler);
            var package = CreateCudaRuntimePackage(archive, wheel) with { ArchiveSha256 = new string('0', 64) };
            var installer = new SherpaCudaRuntimeInstaller(tempDir, client, package);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => installer.EnsureInstalledAsync(CancellationToken.None));

            Assert.Equal("The downloaded sherpa-onnx CUDA runtime did not match the expected checksum.", error.Message);
            Assert.False(installer.IsInstalled);
            // Nothing was extracted, the wheel was never requested and the rejected download is gone.
            Assert.Equal([TestArchiveUrl], handler.RequestedUrls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(installer.RuntimeDirectory));
            Assert.Equal(["native"], ListRuntimeRootEntries(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CudaRuntimeInstaller_RejectsWheelThatDoesNotMatchThePinnedHash()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            var archive = CreateSherpaCudaArchive();
            var wheel = CreateCublasWheel();
            var handler = new PinnedDownloadHandler(archive, wheel);
            using var client = new HttpClient(handler);
            var package = CreateCudaRuntimePackage(archive, wheel);
            var tampered = package with { Dependencies = [package.Dependencies[0] with { Sha256 = new string('0', 64) }] };
            var installer = new SherpaCudaRuntimeInstaller(tempDir, client, tampered);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => installer.EnsureInstalledAsync(CancellationToken.None));

            Assert.Equal(
                "The downloaded CUDA dependency package nvidia-cublas-cu12 1.0.0 did not match the expected checksum.",
                error.Message);
            Assert.False(installer.IsInstalled);
            // Only the verified archive reached the runtime directory; the rejected wheel left no DLL behind.
            Assert.Equal(
                ["onnxruntime.dll", "onnxruntime_providers_cuda.dll", "sherpa-onnx-c-api.dll", "sherpaort.dll"],
                Directory.GetFiles(installer.RuntimeDirectory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
            Assert.Equal(["installed.json", "native"], ListRuntimeRootEntries(tempDir));

            // The verified archive stays installed, so a retry with the correct pin resumes with the wheel alone.
            var repaired = new SherpaCudaRuntimeInstaller(tempDir, client, package);
            await repaired.EnsureInstalledAsync(CancellationToken.None);
            Assert.True(repaired.IsInstalled);
            Assert.Equal([TestArchiveUrl, TestWheelUrl, TestWheelUrl], handler.RequestedUrls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CudaRuntimeInstaller_KeepsCompleteInstallationsMadeBeforeReceipts()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            // An installation made before downloads were verified: every file is present and patched,
            // but there is no receipt. It must keep working without any download or write.
            var nativeDir = SeedInstalledRuntimeFiles(tempDir);
            var archive = CreateSherpaCudaArchive();
            var wheel = CreateCublasWheel();
            var handler = new PinnedDownloadHandler(archive, wheel);
            using var client = new HttpClient(handler);
            var installer = new SherpaCudaRuntimeInstaller(tempDir, client, CreateCudaRuntimePackage(archive, wheel));

            Assert.True(installer.IsInstalled);
            await installer.EnsureInstalledAsync(CancellationToken.None);

            Assert.True(installer.IsInstalled);
            Assert.Empty(handler.RequestedUrls);
            Assert.Equal(["native"], ListRuntimeRootEntries(tempDir));
            Assert.Equal("unverified", File.ReadAllText(Path.Join(nativeDir, "cublas64_12.dll")));
            Assert.Equal("unverified", File.ReadAllText(Path.Join(nativeDir, "onnxruntime.dll")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CudaRuntimeInstaller_ReinstallsWhenTheReceiptDisagreesWithThePins()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            // A receipt from an earlier pin: the wheel still matches, the archive does not.
            var nativeDir = SeedInstalledRuntimeFiles(tempDir);
            var archive = CreateSherpaCudaArchive();
            var wheel = CreateCublasWheel();
            File.WriteAllText(
                Path.Join(tempDir, "Runtimes", "sherpa-onnx-cuda", "test-v1", "installed.json"),
                JsonSerializer.Serialize(new
                {
                    Version = "test-v1",
                    ArchiveSha256 = new string('0', 64),
                    WheelSha256 = new Dictionary<string, string> { ["nvidia-cublas-cu12"] = Sha256Hex(wheel) }
                }));
            var handler = new PinnedDownloadHandler(archive, wheel);
            using var client = new HttpClient(handler);
            var installer = new SherpaCudaRuntimeInstaller(tempDir, client, CreateCudaRuntimePackage(archive, wheel));

            Assert.False(installer.IsInstalled);
            await installer.EnsureInstalledAsync(CancellationToken.None);

            Assert.True(installer.IsInstalled);
            // Only the artifact whose pin changed is downloaded again.
            Assert.Equal([TestArchiveUrl], handler.RequestedUrls);
            Assert.Equal("onnxruntime", File.ReadAllText(Path.Join(nativeDir, "onnxruntime.dll")));
            Assert.Equal("unverified", File.ReadAllText(Path.Join(nativeDir, "cublas64_12.dll")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
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
    public void NativeRuntime_PreloadsCudnnComponentsByAbsolutePathOnce()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cudnn-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (var fileName in SherpaOnnxNativeRuntime.CudaPreloadFileNames)
                File.WriteAllBytes(Path.Join(tempDir, fileName), []);

            var loadedPaths = new List<string>();
            var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

            SherpaOnnxNativeRuntime.PreloadCudaDependencies(
                tempDir,
                path =>
                {
                    loadedPaths.Add(path);
                    return new IntPtr(loadedPaths.Count);
                },
                handles);
            SherpaOnnxNativeRuntime.PreloadCudaDependencies(
                tempDir,
                path =>
                {
                    loadedPaths.Add(path);
                    return new IntPtr(loadedPaths.Count);
                },
                handles);

            Assert.Equal(SherpaOnnxNativeRuntime.CudaPreloadFileNames.Count, loadedPaths.Count);
            Assert.Equal(
                SherpaOnnxNativeRuntime.CudaPreloadFileNames,
                loadedPaths.Select(Path.GetFileName));
            Assert.All(loadedPaths, path => Assert.True(Path.IsPathFullyQualified(path)));
            Assert.Equal(loadedPaths.Count, handles.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
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

    private const string TestArchiveUrl = "https://example.test/sherpa-onnx-cuda.tar.bz2";
    private const string TestWheelUrl = "https://example.test/nvidia_cublas_cu12-1.0.0-py3-none-win_amd64.whl";
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

    private static SherpaCudaRuntimePackage CreateCudaRuntimePackage(byte[] archive, byte[] wheel) => new(
        "test-v1",
        TestArchiveUrl,
        Sha256Hex(archive),
        [new CudaDependencyPackage("nvidia-cublas-cu12", "1.0.0", TestWheelUrl, Sha256Hex(wheel), ["cublas64_12.dll"])]);

    // The real asset is a tar.bz2 whose native directory is found by sherpa-onnx-c-api.dll.
    private static byte[] CreateSherpaCudaArchive() => CreateTarBz2(
        ("sherpa-onnx-test/lib/sherpa-onnx-c-api.dll", "prefix onnxruntime.dll\0 suffix"),
        ("sherpa-onnx-test/lib/onnxruntime.dll", "onnxruntime"),
        ("sherpa-onnx-test/lib/onnxruntime_providers_cuda.dll", "cuda-provider"));

    private static byte[] CreateCublasWheel() => CreateZipArchive(
        ("nvidia/cublas/bin/cublas64_12.dll", "cublas"),
        ("nvidia_cublas_cu12-1.0.0.dist-info/RECORD", "ignore"));

    private static byte[] CreateTarBz2(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = WriterFactory.OpenWriter(output, ArchiveType.Tar, new WriterOptions(CompressionType.BZip2)))
        {
            foreach (var (path, content) in entries)
            {
                using var source = new MemoryStream(Encoding.ASCII.GetBytes(content));
                writer.Write(path, source, null);
            }
        }

        return output.ToArray();
    }

    private static byte[] CreateZipArchive(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(Encoding.ASCII.GetBytes(content));
            }
        }

        return output.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Lays out a complete, patched runtime directory the way the installer leaves it after a
    // successful installation, without a receipt.
    private static string SeedInstalledRuntimeFiles(string pluginDataDirectory)
    {
        var nativeDir = Path.Join(pluginDataDirectory, "Runtimes", "sherpa-onnx-cuda", "test-v1", "native");
        Directory.CreateDirectory(nativeDir);
        foreach (var fileName in new[] { "onnxruntime.dll", "onnxruntime_providers_cuda.dll", "sherpaort.dll", "cublas64_12.dll" })
            File.WriteAllText(Path.Join(nativeDir, fileName), "unverified");
        File.WriteAllBytes(
            Path.Join(nativeDir, "sherpa-onnx-c-api.dll"),
            Encoding.ASCII.GetBytes("prefix sherpaort.dll\0\0\0 suffix"));
        return nativeDir;
    }

    private static IEnumerable<string> ListRuntimeRootEntries(string pluginDataDirectory) =>
        Directory.EnumerateFileSystemEntries(Path.Join(pluginDataDirectory, "Runtimes", "sherpa-onnx-cuda", "test-v1"))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal);

    private sealed class PinnedDownloadHandler(byte[] archive, byte[] wheel) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var body = url switch
            {
                TestArchiveUrl => archive,
                TestWheelUrl => wheel,
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

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

    private sealed class FakeCudaRuntimeInstaller : ISherpaCudaRuntimeInstaller
    {
        private readonly Exception? _installException;

        public FakeCudaRuntimeInstaller(bool isInstalled, Exception? installException = null)
        {
            IsInstalled = isInstalled;
            _installException = installException;
        }

        public bool EnsureInstalledCalled { get; private set; }
        public bool IsInstalled { get; private set; }
        public string? RuntimeDirectory => IsInstalled ? Path.Join(Path.GetTempPath(), "sherpa-cuda-runtime") : null;

        public Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            EnsureInstalledCalled = true;
            if (_installException is not null)
                throw _installException;

            IsInstalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCudaRuntimeProbe(CudaRuntimeProbeResult result) : ISherpaCudaRuntimeProbe
    {
        public int CallCount { get; private set; }

        public Task<CudaRuntimeProbeResult> ProbeAsync(
            string modelId,
            string modelDirectory,
            string runtimeDirectory,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
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

// Exercise supported CUDA behavior on Windows x64 and its rejection elsewhere.
// The CPU and package tests remain part of both platform runs.
public sealed class CudaPlatformFactAttribute : FactAttribute
{
    public CudaPlatformFactAttribute(bool supported = true)
    {
        var available = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        if (available != supported)
            Skip = supported ? "CUDA provider requires Windows x64." : "Unsupported-platform check runs outside Windows x64.";
    }
}
