using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.CohereTranscribe;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CohereTranscribePluginTests
{
    [Fact]
    public void ManifestAndPluginMetadata_AreLocalAndVersionMatched()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            TestFile.ReadProjectFile(
                "plugins",
                "TypeWhisper.Plugin.CohereTranscribe",
                "manifest.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var sut = new CohereTranscribePlugin();

        Assert.NotNull(manifest);
        Assert.Equal("com.typewhisper.cohere-transcribe", manifest.Id);
        Assert.Equal("transcription", manifest.Category);
        Assert.True(manifest.IsLocal);
        Assert.Equal("1.0.6", manifest.MinHostVersion);
        Assert.Equal(manifest.Version, sut.PluginVersion);
        Assert.Equal(manifest.Id, sut.PluginId);
        Assert.Equal("cohere-transcribe", sut.ProviderId);
        Assert.True(sut.SupportsModelDownload);
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public void ModelMetadata_ExposesFourPinnedQuantizationsAndFourteenLanguages()
    {
        var sut = new CohereTranscribePlugin();
        var models = sut.TranscriptionModels;

        Assert.Equal(
            [
                "cohere-transcribe-03-2026-q4_k",
                "cohere-transcribe-03-2026-q5_0",
                "cohere-transcribe-03-2026-q6_k",
                "cohere-transcribe-03-2026-q8_0"
            ],
            models.Select(model => model.Id));
        Assert.Equal(
            "cohere-transcribe-03-2026-q5_0",
            Assert.Single(models, model => model.IsRecommended).Id);
        Assert.All(models, model => Assert.Equal(14, model.LanguageCount));
        Assert.Equal(14, sut.SupportedLanguages.Count);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("ja", sut.SupportedLanguages);
        Assert.DoesNotContain("ru", sut.SupportedLanguages);
    }

    [Fact]
    public void DownloadDefinitions_AreRevisionPinnedAndSha256Pinned()
    {
        const string modelRoot =
            "https://huggingface.co/cstr/cohere-transcribe-03-2026-GGUF/resolve/"
            + "2242638d5dfecc6f1dbe6c3a8713b97deb2e150f/";
        var expectedModels = new (string Id, RemoteArtifact Artifact)[]
        {
            (
                "cohere-transcribe-03-2026-q4_k",
                new RemoteArtifact(
                    "cohere-transcribe-q4_k.gguf",
                    modelRoot + "cohere-transcribe-q4_k.gguf",
                    1_510_362_752,
                    "2931fc0ac6d6708eef5389aadf1ebd5eec7b8e764bac385be585e910c0e7b410")),
            (
                "cohere-transcribe-03-2026-q5_0",
                new RemoteArtifact(
                    "cohere-transcribe-q5_0.gguf",
                    modelRoot + "cohere-transcribe-q5_0.gguf",
                    1_738_722_944,
                    "a09696c5cc2ed5052bf290c4f2beb35abc69c0d6986842042d92bebb22c9184e")),
            (
                "cohere-transcribe-03-2026-q6_k",
                new RemoteArtifact(
                    "cohere-transcribe-q6_k.gguf",
                    modelRoot + "cohere-transcribe-q6_k.gguf",
                    1_981_355_648,
                    "0ad2634e0ba34efa38a47d4fd4cf34d7a2d738d8486d83b8d5a178f823109c52")),
            (
                "cohere-transcribe-03-2026-q8_0",
                new RemoteArtifact(
                    "cohere-transcribe-q8_0.gguf",
                    modelRoot + "cohere-transcribe-q8_0.gguf",
                    2_423_803_520,
                    "c8620cb182a7c04e311e6c24e478b94f7ecd7f1b5230bf39fffa8daf94644f51"))
        };
        var actualModels = CohereModelCatalog.All
            .Select(model => (model.Id, model.Artifact))
            .ToArray();

        Assert.Equal(expectedModels, actualModels);
        Assert.Equal(
            new RemoteArtifact(
                "ggml-silero-v6.2.0.bin",
                "https://huggingface.co/ggml-org/whisper-vad/resolve/"
                + "9ffd54a1e1ee413ddf265af9913beaf518d1639b/"
                + "ggml-silero-v6.2.0.bin",
                885_098,
                "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"),
            CohereLocalAssetManager.VadModel);
        Assert.Equal(
            new RemoteArtifact(
                "ecapa-lid-107-f16.gguf",
                "https://huggingface.co/cstr/ecapa-lid-107-GGUF/resolve/"
                + "95fb0613bf78c6e48305fccd9ce023ac15f0b5a6/"
                + "ecapa-lid-107-f16.gguf",
                42_838_944,
                "59db30ba67cec2f36304f794420779c181124332246f75fc66c349f184110340"),
            CohereLocalAssetManager.LanguageIdModel);
        Assert.Equal(
            new RuntimePackage(
                CrispAsrBackend.Cpu,
                "cpu",
                new RemoteArtifact(
                    "crispasr-windows-x86_64-cpu.zip",
                    "https://github.com/CrispStrobe/CrispASR/releases/download/"
                    + "v0.8.24/crispasr-windows-x86_64-cpu.zip",
                    7_635_428,
                    "a05456c9adac276289060ae83bd77ed7b4d87ccfd447aed804e497d76e73f8f8")),
            CohereLocalAssetManager.CpuRuntime);
        Assert.Equal(
            new RuntimePackage(
                CrispAsrBackend.Cuda,
                "cuda",
                new RemoteArtifact(
                    "crispasr-windows-x86_64-cuda.zip",
                    "https://github.com/CrispStrobe/CrispASR/releases/download/"
                    + "v0.8.24/crispasr-windows-x86_64-cuda.zip",
                    729_126_487,
                    "832af6218508ac52fc71ac5653c433786bdfae81f775e2c77bfefd05df6f255b")),
            CohereLocalAssetManager.CudaRuntime);
        Assert.Equal(
            new RuntimePackage(
                CrispAsrBackend.Vulkan,
                "vulkan",
                new RemoteArtifact(
                    "crispasr-windows-x86_64-vulkan.zip",
                    "https://github.com/CrispStrobe/CrispASR/releases/download/"
                    + "v0.8.24/crispasr-windows-x86_64-vulkan.zip",
                    35_580_185,
                    "70956638045042b49f61f9f04ee9ceae9fc8b450f6f56a10fc98c2088207628a")),
            CohereLocalAssetManager.VulkanRuntime);
    }

    [Fact]
    public void ModelPaths_KeepQuantizationsSeparateAndReuseAuxiliaryModels()
    {
        using var temp = new TempDirectory();
        using var sut = new CohereLocalAssetManager(temp.Path);

        var paths = CohereModelCatalog.All
            .ToDictionary(model => model.Id, model => sut.GetModelPaths(model.Id));
        var defaultPaths = paths[CohereModelCatalog.DefaultModelId];

        Assert.Equal(
            CohereModelCatalog.All.Select(model => model.Artifact.FileName),
            CohereModelCatalog.All.Select(model => Path.GetFileName(paths[model.Id].ModelPath)));
        Assert.Equal(
            CohereModelCatalog.All.Count,
            paths.Values.Select(path => path.ModelPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            paths.Values,
            path =>
            {
                Assert.Equal(defaultPaths.VadModelPath, path.VadModelPath);
                Assert.Equal(defaultPaths.LanguageIdModelPath, path.LanguageIdModelPath);
                Assert.Equal(defaultPaths.CacheDirectory, path.CacheDirectory);
            });
    }

    [Fact]
    public void SelectModel_AcceptsPublishedQuantizationsAndRejectsUnknownIds()
    {
        using var sut = new CohereTranscribePlugin();

        foreach (var model in CohereModelCatalog.All)
        {
            sut.SelectModel(model.Id);
            Assert.Equal(model.Id, sut.SelectedModelId);
        }

        Assert.Throws<ArgumentException>(() => sut.SelectModel("cohere-unknown"));
    }

    [Fact]
    public async Task DownloadVerifiedAsync_ResumesWithVerifiedRangeChunks()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("cohere-local");
        var destination = Path.Join(temp.Path, "model.gguf.download");
        await File.WriteAllBytesAsync(destination, payload[..3]);

        var handler = new RangeDownloadHandler(payload);
        using var httpClient = new HttpClient(handler);
        using var sut = new CohereLocalAssetManager(
            temp.Path,
            httpClient,
            downloadChunkSizeBytes: 4,
            downloadInactivityTimeout: TimeSpan.FromSeconds(5),
            maxDownloadAttempts: 2,
            downloadRetryDelay: TimeSpan.Zero);
        var artifact = new RemoteArtifact(
            "model.gguf",
            "https://downloads.example/model.gguf",
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)));

        await sut.DownloadVerifiedAsync(
            artifact,
            destination,
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(["bytes=3-6", "bytes=7-10", "bytes=11-11"], handler.RequestedRanges);
    }

    [Fact]
    public async Task DownloadVerifiedAsync_RetriesTransientRangeFailure()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("retry-local");
        var destination = Path.Join(temp.Path, "model.gguf.download");
        var handler = new RangeDownloadHandler(payload, failuresRemaining: 1);
        using var httpClient = new HttpClient(handler);
        using var sut = new CohereLocalAssetManager(
            temp.Path,
            httpClient,
            downloadChunkSizeBytes: 64,
            downloadInactivityTimeout: TimeSpan.FromSeconds(5),
            maxDownloadAttempts: 2,
            downloadRetryDelay: TimeSpan.Zero);
        var artifact = new RemoteArtifact(
            "model.gguf",
            "https://downloads.example/model.gguf",
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)));

        await sut.DownloadVerifiedAsync(
            artifact,
            destination,
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadVerifiedAsync_AcceptsCompletedRangeAfterLateStreamFailure()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("completed-before-dispose");
        var destination = Path.Join(temp.Path, "model.gguf.download");
        var handler = new RangeDownloadHandler(payload, disposeFailuresRemaining: 1);
        using var httpClient = new HttpClient(handler);
        using var sut = new CohereLocalAssetManager(
            temp.Path,
            httpClient,
            downloadChunkSizeBytes: 64,
            downloadInactivityTimeout: TimeSpan.FromSeconds(5),
            maxDownloadAttempts: 2,
            downloadRetryDelay: TimeSpan.Zero);
        var artifact = new RemoteArtifact(
            "model.gguf",
            "https://downloads.example/model.gguf",
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)));

        await sut.DownloadVerifiedAsync(
            artifact,
            destination,
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadVerifiedAsync_SendsTokenOnlyToHuggingFace()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("authenticated-local");
        var handler = new RangeDownloadHandler(payload);
        using var httpClient = new HttpClient(handler);
        using var sut = new CohereLocalAssetManager(
            temp.Path,
            httpClient,
            downloadChunkSizeBytes: 64,
            downloadInactivityTimeout: TimeSpan.FromSeconds(5),
            maxDownloadAttempts: 2,
            downloadRetryDelay: TimeSpan.Zero);
        sut.SetHuggingFaceToken("hf_read_only_test");

        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        await sut.DownloadVerifiedAsync(
            new RemoteArtifact(
                "model.gguf",
                "https://huggingface.co/example/model/resolve/revision/model.gguf",
                payload.Length,
                hash),
            Path.Join(temp.Path, "model.gguf.download"),
            progress: null,
            CancellationToken.None);
        await sut.DownloadVerifiedAsync(
            new RemoteArtifact(
                "runtime.zip",
                "https://github.com/example/runtime/releases/download/v1/runtime.zip",
                payload.Length,
                hash),
            Path.Join(temp.Path, "runtime.zip.download"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("huggingface.co", handler.Requests[0].Host);
        Assert.Equal("Bearer", handler.Requests[0].AuthorizationScheme);
        Assert.Equal("hf_read_only_test", handler.Requests[0].AuthorizationParameter);
        Assert.Equal("github.com", handler.Requests[1].Host);
        Assert.Null(handler.Requests[1].AuthorizationScheme);
        Assert.Null(handler.Requests[1].AuthorizationParameter);
    }

    [Theory]
    [InlineData(TranscriptionAccelerationPreference.Auto, true, true, "Cuda,Vulkan,Cpu")]
    [InlineData(TranscriptionAccelerationPreference.Auto, false, true, "Vulkan,Cpu")]
    [InlineData(TranscriptionAccelerationPreference.Auto, false, false, "Cpu")]
    [InlineData(TranscriptionAccelerationPreference.Cpu, false, false, "Cpu")]
    [InlineData(TranscriptionAccelerationPreference.NvidiaCuda, false, false, "Cuda")]
    [InlineData(TranscriptionAccelerationPreference.AmdVulkan, false, false, "Vulkan")]
    public void ResolveBackendCandidates_MapsPreferencesAndAutomaticFallbackOrder(
        TranscriptionAccelerationPreference preference,
        bool cudaAvailable,
        bool vulkanAvailable,
        string expected)
    {
        var actual = CohereTranscribePlugin.ResolveBackendCandidates(
            preference,
            cudaAvailable,
            vulkanAvailable);

        Assert.Equal(expected, string.Join(",", actual));
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("de-DE", "de")]
    [InlineData("PT_br", "pt")]
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData("ru", null)]
    public void NormalizeLanguageOrNull_UsesOnlySupportedPrimaryLanguageCodes(
        string input,
        string? expected)
    {
        Assert.Equal(expected, CohereTranscribePlugin.NormalizeLanguageOrNull(input));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    [InlineData(64, 12)]
    public void GetRecommendedThreadCount_UsesPhysicalCoreApproximationWithCap(
        int logicalProcessors,
        int expected)
    {
        Assert.Equal(
            expected,
            CohereTranscribePlugin.GetRecommendedThreadCount(logicalProcessors));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  hf_example  ", "hf_example")]
    public void NormalizeHuggingFaceToken_TrimsAndKeepsOptionalValues(
        string? token,
        string? expected)
    {
        Assert.Equal(expected, CohereTranscribePlugin.NormalizeHuggingFaceToken(token));
    }

    [Theory]
    [InlineData("hf_bad token")]
    [InlineData("hf_bad\ntoken")]
    public void NormalizeHuggingFaceToken_RejectsWhitespaceInsideToken(string token)
    {
        Assert.Throws<ArgumentException>(() =>
            CohereTranscribePlugin.NormalizeHuggingFaceToken(token));
    }

    [Fact]
    public void BuildStartInfo_IsLoopbackOnlyAuthenticatedAndUsesManagedAuxiliaryModels()
    {
        var paths = new CohereModelPaths(
            @"C:\models\cohere.gguf",
            @"C:\models\vad.bin",
            @"C:\models\lid.gguf",
            @"C:\models\cache");
        var configuration = new CrispAsrServerConfiguration(
            CohereTranscribePlugin.ModelId,
            @"C:\runtime\crispasr.exe",
            paths,
            CrispAsrBackend.Cuda,
            8);
        const string apiKey = "not-on-the-command-line";

        var startInfo = CrispAsrServer.BuildStartInfo(configuration, 43123, apiKey);
        var arguments = startInfo.Arguments;

        Assert.Equal(Path.Join(Environment.SystemDirectory, "cmd.exe"), startInfo.FileName);
        Assert.StartsWith("/d /s /v:off /c ", arguments, StringComparison.Ordinal);
        Assert.Contains("\"%TYPEWHISPER_CRISPASR_EXECUTABLE%\"", arguments);
        Assert.Contains("--host 127.0.0.1", arguments);
        Assert.Contains("--port 43123", arguments);
        Assert.Contains("--backend cohere", arguments);
        Assert.Contains("--model \"%TYPEWHISPER_CRISPASR_MODEL%\"", arguments);
        Assert.Contains("--lid-backend ecapa", arguments);
        Assert.Contains("--lid-model \"%TYPEWHISPER_CRISPASR_LID_MODEL%\"", arguments);
        Assert.Contains("--vad-model \"%TYPEWHISPER_CRISPASR_VAD_MODEL%\"", arguments);
        Assert.Contains("--gpu-backend cuda", arguments);
        Assert.Contains("--strict-pipeline", arguments);
        Assert.Contains("--require-vad", arguments);
        Assert.DoesNotContain("--api-keys", arguments);
        Assert.DoesNotContain(apiKey, arguments);
        Assert.Equal(@"C:\runtime", startInfo.WorkingDirectory);
        Assert.Equal(apiKey, startInfo.Environment["CRISPASR_API_KEYS"]);
        Assert.Equal(paths.CacheDirectory, startInfo.Environment["CRISPASR_CACHE_DIR"]);
        Assert.Equal(
            configuration.ExecutablePath,
            startInfo.Environment["TYPEWHISPER_CRISPASR_EXECUTABLE"]);
        Assert.Equal(paths.ModelPath, startInfo.Environment["TYPEWHISPER_CRISPASR_MODEL"]);
        Assert.Equal(paths.LanguageIdModelPath, startInfo.Environment["TYPEWHISPER_CRISPASR_LID_MODEL"]);
        Assert.Equal(paths.VadModelPath, startInfo.Environment["TYPEWHISPER_CRISPASR_VAD_MODEL"]);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    }

    [Fact]
    public void ResolveUnpackagedChildPath_MapsMsixRedirectedLocalAppDataOnlyOnce()
    {
        const string localAppData = @"C:\Users\tester\AppData\Local";
        const string packageFamilyName = "TypeWhisper.TypeWhisper_51tqb5623pxja";
        var physicalLocalAppData = Path.Join(
            localAppData,
            "Packages",
            packageFamilyName,
            "LocalCache",
            "Local");
        var logicalPath = Path.Join(
            localAppData,
            "TypeWhisper-UserData",
            "PluginData",
            "com.typewhisper.cohere-transcribe",
            "runtime",
            "crispasr.exe");
        var physicalPath = Path.Join(
            physicalLocalAppData,
            Path.GetRelativePath(localAppData, logicalPath));

        Assert.Equal(
            physicalPath,
            CrispAsrServer.ResolveUnpackagedChildPath(
                logicalPath,
                localAppData,
                physicalLocalAppData));
        Assert.Equal(
            physicalPath,
            CrispAsrServer.ResolveUnpackagedChildPath(
                physicalPath,
                localAppData,
                physicalLocalAppData));
        Assert.Equal(
            @"D:\Models\cohere.gguf",
            CrispAsrServer.ResolveUnpackagedChildPath(
                @"D:\Models\cohere.gguf",
                localAppData,
                physicalLocalAppData));
    }

    [Fact]
    public async Task WindowsProcessJob_DisposeTerminatesAssignedProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Join(Environment.SystemDirectory, "ping.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("30");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("1000");

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            using (WindowsProcessJob.CreateAndAssign(process))
            {
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (started && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsPathTraversal()
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Join(temp.Path, "malicious.zip");
        var destination = Path.Join(temp.Path, "runtime");
        var escapedPath = Path.Join(temp.Path, "escaped.txt");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("must not escape");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CohereLocalAssetManager.ExtractArchiveAsync(
                archivePath,
                destination,
                CancellationToken.None));
        Assert.False(File.Exists(escapedPath));
    }

    [Fact]
    public async Task RemoveModelAsync_DeletesModelArtifactsAndKeepsSharedAssets()
    {
        using var temp = new TempDirectory();
        using var sut = new CohereLocalAssetManager(temp.Path);
        var paths = sut.GetModelPaths(CohereModelCatalog.DefaultModelId);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ModelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.VadModelPath)!);
        await File.WriteAllTextAsync(paths.ModelPath, "model");
        await File.WriteAllTextAsync(paths.ModelPath + ".sha256", "hash");
        await File.WriteAllTextAsync(paths.ModelPath + ".download", "partial");
        await File.WriteAllTextAsync(paths.VadModelPath, "vad");
        await File.WriteAllTextAsync(paths.LanguageIdModelPath, "language-id");

        await sut.RemoveModelAsync(CohereModelCatalog.DefaultModelId, CancellationToken.None);

        Assert.False(File.Exists(paths.ModelPath));
        Assert.False(File.Exists(paths.ModelPath + ".sha256"));
        Assert.False(File.Exists(paths.ModelPath + ".download"));
        Assert.True(File.Exists(paths.VadModelPath));
        Assert.True(File.Exists(paths.LanguageIdModelPath));
    }

    [Theory]
    [InlineData(CohereModelCatalog.Q4KModelId)]
    [InlineData(CohereModelCatalog.DefaultModelId)]
    [InlineData(CohereModelCatalog.Q6KModelId)]
    [InlineData(CohereModelCatalog.Q8ModelId)]
    public async Task PluginLifecycle_UsesSelectedQuantizationAndManagedCpuRuntime(string modelId)
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        var server = new FakeCrispAsrServer();
        using var sut = new CohereTranscribePlugin(assets, server);
        var host = new FakePluginHostServices(temp.Path);
        var downloadProgress = new List<double>();
        var expectedModelPath = assets.GetModelPaths(modelId).ModelPath;

        await sut.ActivateAsync(host);
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await sut.DownloadModelAsync(
            modelId,
            new InlineProgress<double>(downloadProgress.Add),
            CancellationToken.None);
        await sut.LoadModelAsync(modelId, CancellationToken.None);
        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de-DE",
            translate: false,
            prompt: "ignored",
            CancellationToken.None);

        Assert.True(assets.ModelInstalled);
        Assert.Equal(modelId, assets.LastEnsuredModelId);
        Assert.Equal([CrispAsrBackend.Cpu], assets.EnsuredRuntimes);
        Assert.Equal(modelId, server.LastConfiguration?.ModelId);
        Assert.Equal(expectedModelPath, server.LastConfiguration?.ModelPaths.ModelPath);
        Assert.Equal(CrispAsrBackend.Cpu, server.LastConfiguration?.Backend);
        Assert.Equal("de", server.LastLanguage);
        Assert.Equal("lokal", result.Text);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Equal("Using CPU", sut.AccelerationStatus.DisplayText);
        Assert.Contains(downloadProgress, value => value >= 1);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.TranscribeAsync(
                [1],
                "de",
                translate: true,
                prompt: null,
                CancellationToken.None));

        await sut.UnloadModelAsync();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task PluginRemoveModelAsync_StopsActiveSidecarAndRemovesSelectedQuantization()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        var server = new FakeCrispAsrServer();
        using var sut = new CohereTranscribePlugin(assets, server);
        await sut.ActivateAsync(new FakePluginHostServices(temp.Path));
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await sut.DownloadModelAsync(
            CohereModelCatalog.DefaultModelId,
            progress: null,
            CancellationToken.None);
        await sut.LoadModelAsync(CohereModelCatalog.DefaultModelId, CancellationToken.None);

        await sut.RemoveModelAsync(CohereModelCatalog.DefaultModelId, CancellationToken.None);

        Assert.True(sut.SupportsModelRemoval);
        Assert.False(server.IsRunning);
        Assert.False(assets.IsModelInstalled(CohereModelCatalog.DefaultModelId));
        Assert.Contains(CrispAsrBackend.Cpu, assets.EnsuredRuntimes);
    }

    [Fact]
    public async Task TranscribeAsync_RestartsUnexpectedlyStoppedSidecar()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        var server = new FakeCrispAsrServer();
        using var sut = new CohereTranscribePlugin(assets, server);
        var host = new FakePluginHostServices(temp.Path);

        await sut.ActivateAsync(host);
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await sut.DownloadModelAsync(
            CohereModelCatalog.DefaultModelId,
            progress: null,
            CancellationToken.None);
        await sut.LoadModelAsync(CohereModelCatalog.DefaultModelId, CancellationToken.None);
        Assert.Equal(1, server.StartCount);

        await server.StopAsync();
        Assert.Equal("Not loaded", sut.AccelerationStatus.DisplayText);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None);

        Assert.Equal("lokal", result.Text);
        Assert.True(server.IsRunning);
        Assert.Equal(2, server.StartCount);
        Assert.Equal(CohereModelCatalog.DefaultModelId, server.LastConfiguration?.ModelId);
        Assert.Equal("Using CPU", sut.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task TranscribeAsync_RestartsSidecarThatExitsDuringRequest()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        var server = new FakeCrispAsrServer();
        using var sut = new CohereTranscribePlugin(assets, server);

        await sut.ActivateAsync(new FakePluginHostServices(temp.Path));
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await sut.DownloadModelAsync(
            CohereModelCatalog.DefaultModelId,
            progress: null,
            CancellationToken.None);
        await sut.LoadModelAsync(CohereModelCatalog.DefaultModelId, CancellationToken.None);
        server.FailNextTranscriptionAndStop = true;

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None);

        Assert.Equal("lokal", result.Text);
        Assert.True(server.IsRunning);
        Assert.Equal(2, server.StartCount);
        Assert.Equal("Using CPU", sut.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task PluginLifecycle_LoadsStoresAndRemovesOptionalHuggingFaceToken()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        using var sut = new CohereTranscribePlugin(assets, new FakeCrispAsrServer());
        var host = new FakePluginHostServices(
            temp.Path,
            new Dictionary<string, string>
            {
                [CohereTranscribePlugin.HuggingFaceTokenSecretName] = "  hf_saved  "
            });

        await sut.ActivateAsync(host);

        Assert.Equal("hf_saved", sut.HuggingFaceToken);
        Assert.Equal("hf_saved", assets.HuggingFaceToken);
        Assert.True(sut.IsConfigured);

        await sut.SetHuggingFaceTokenAsync("  hf_replaced  ");

        Assert.Equal("hf_replaced", sut.HuggingFaceToken);
        Assert.Equal("hf_replaced", assets.HuggingFaceToken);
        Assert.Equal(
            "hf_replaced",
            host.Secrets[CohereTranscribePlugin.HuggingFaceTokenSecretName]);

        await sut.SetHuggingFaceTokenAsync("");

        Assert.Null(sut.HuggingFaceToken);
        Assert.Null(assets.HuggingFaceToken);
        Assert.DoesNotContain(
            CohereTranscribePlugin.HuggingFaceTokenSecretName,
            host.Secrets);
    }

    [Fact]
    public async Task ActivateAsync_IgnoresAndRemovesMalformedStoredHuggingFaceToken()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        using var sut = new CohereTranscribePlugin(assets, new FakeCrispAsrServer());
        var host = new FakePluginHostServices(
            temp.Path,
            new Dictionary<string, string>
            {
                [CohereTranscribePlugin.HuggingFaceTokenSecretName] = "hf_bad token"
            });

        await sut.ActivateAsync(host);

        Assert.Null(sut.HuggingFaceToken);
        Assert.Null(assets.HuggingFaceToken);
        Assert.DoesNotContain(
            CohereTranscribePlugin.HuggingFaceTokenSecretName,
            host.Secrets);
        Assert.Contains(
            host.Logs,
            entry => entry.Level == PluginLogLevel.Warning
                && entry.Message.Contains("malformed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadRequirements_ExposeAndPersistOptionalHuggingFaceToken()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        using var validationClient = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.OK,
            "{\"name\":\"typewhisper\"}"));
        using var sut = new CohereTranscribePlugin(
            assets,
            new FakeCrispAsrServer(),
            validationClient);
        var host = new FakePluginHostServices(temp.Path);
        var changeCount = 0;
        sut.ModelDownloadRequirementsChanged += (_, _) => changeCount++;
        await sut.ActivateAsync(host);

        var initial = Assert.Single(
            sut.ModelDownloadRequirements,
            requirement => requirement.ModelId == CohereModelCatalog.DefaultModelId);
        Assert.Equal(PluginModelDownloadRequirementKind.Credential, initial.Kind);
        Assert.False(initial.IsRequired);
        Assert.False(initial.IsSatisfied);
        Assert.Null(sut.CreateSettingsView());

        var result = await sut.SaveModelDownloadCredentialAsync(
            CohereModelCatalog.DefaultModelId,
            CohereTranscribePlugin.HuggingFaceTokenRequirementId,
            "  hf_valid  ",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("hf_valid", assets.HuggingFaceToken);
        Assert.Equal("hf_valid", host.Secrets[CohereTranscribePlugin.HuggingFaceTokenSecretName]);
        Assert.True(Assert.Single(
            sut.ModelDownloadRequirements,
            requirement => requirement.ModelId == CohereModelCatalog.DefaultModelId).IsSatisfied);
        Assert.Equal(1, changeCount);

        await sut.ClearModelDownloadCredentialAsync(
            CohereModelCatalog.DefaultModelId,
            CohereTranscribePlugin.HuggingFaceTokenRequirementId,
            CancellationToken.None);

        Assert.False(Assert.Single(
            sut.ModelDownloadRequirements,
            requirement => requirement.ModelId == CohereModelCatalog.DefaultModelId).IsSatisfied);
        Assert.DoesNotContain(CohereTranscribePlugin.HuggingFaceTokenSecretName, host.Secrets);
        Assert.Equal(2, changeCount);
    }

    [Fact]
    public async Task SaveModelDownloadCredentialAsync_RejectsInvalidTokenWithoutPersistingIt()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        using var validationClient = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.Unauthorized,
            "{\"error\":\"invalid token\"}"));
        using var sut = new CohereTranscribePlugin(
            assets,
            new FakeCrispAsrServer(),
            validationClient);
        var host = new FakePluginHostServices(temp.Path);
        await sut.ActivateAsync(host);

        var result = await sut.SaveModelDownloadCredentialAsync(
            CohereModelCatalog.DefaultModelId,
            CohereTranscribePlugin.HuggingFaceTokenRequirementId,
            "hf_invalid",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(assets.HuggingFaceToken);
        Assert.Empty(host.Secrets);
    }

    [Fact]
    public async Task SetHuggingFaceTokenAsync_DoesNotApplyTokenWhenSecureStoreFails()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        using var sut = new CohereTranscribePlugin(assets, new FakeCrispAsrServer());
        var host = new FakePluginHostServices(temp.Path)
        {
            StoreSecretException = new InvalidOperationException("secure store failed")
        };
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SetHuggingFaceTokenAsync("hf_new"));

        Assert.Null(sut.HuggingFaceToken);
        Assert.Null(assets.HuggingFaceToken);
        Assert.DoesNotContain(
            CohereTranscribePlugin.HuggingFaceTokenSecretName,
            host.Secrets);
    }

    [Fact]
    public async Task SetHuggingFaceTokenAsync_KeepsExistingTokenWhenSecureDeleteFails()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        using var sut = new CohereTranscribePlugin(assets, new FakeCrispAsrServer());
        var host = new FakePluginHostServices(
            temp.Path,
            new Dictionary<string, string>
            {
                [CohereTranscribePlugin.HuggingFaceTokenSecretName] = "hf_existing"
            })
        {
            DeleteSecretException = new InvalidOperationException("secure delete failed")
        };
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SetHuggingFaceTokenAsync(null));

        Assert.Equal("hf_existing", sut.HuggingFaceToken);
        Assert.Equal("hf_existing", assets.HuggingFaceToken);
        Assert.Equal(
            "hf_existing",
            host.Secrets[CohereTranscribePlugin.HuggingFaceTokenSecretName]);
    }

    [Fact]
    public void PublishWorkflow_MapsCohereTranscribePluginTag()
    {
        var workflow = TestFile.ReadProjectFile(
            ".github",
            "workflows",
            "publish-plugins.yml");

        Assert.Contains(
            "'cohere-transcribe'  = 'TypeWhisper.Plugin.CohereTranscribe'",
            workflow,
            StringComparison.Ordinal);
    }

    private sealed class FakeAssetManager : ICohereLocalAssetManager
    {
        private readonly HashSet<string> _installedModelIds = [];

        public bool ModelInstalled => _installedModelIds.Count > 0;
        public string? LastEnsuredModelId { get; private set; }
        public string? HuggingFaceToken { get; private set; }
        public List<CrispAsrBackend> EnsuredRuntimes { get; } = [];

        public void SetHuggingFaceToken(string? token)
        {
            HuggingFaceToken = token;
        }

        public long GetModelTransferSize(string modelId) => 100;
        public bool IsModelInstalled(string modelId) => _installedModelIds.Contains(modelId);
        public bool IsRuntimeInstalled(CrispAsrBackend backend) =>
            EnsuredRuntimes.Contains(backend);
        public long GetRuntimeTransferSize(CrispAsrBackend backend) => 20;
        public CohereModelPaths GetModelPaths(string modelId) => new(
            $@"C:\models\{modelId}.gguf",
            @"C:\models\vad.bin",
            @"C:\models\lid.gguf",
            @"C:\models\cache");
        public string GetRuntimeExecutablePath(CrispAsrBackend backend) =>
            @"C:\runtime\crispasr.exe";

        public Task EnsureModelAsync(
            string modelId,
            IProgress<ArtifactTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastEnsuredModelId = modelId;
            _installedModelIds.Add(modelId);
            progress?.Report(new ArtifactTransferProgress(100, 100));
            return Task.CompletedTask;
        }

        public Task RemoveModelAsync(string modelId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _installedModelIds.Remove(modelId);
            return Task.CompletedTask;
        }

        public Task EnsureRuntimeAsync(
            CrispAsrBackend backend,
            IProgress<ArtifactTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (!EnsuredRuntimes.Contains(backend))
                EnsuredRuntimes.Add(backend);
            progress?.Report(new ArtifactTransferProgress(20, 20));
            return Task.CompletedTask;
        }
    }

    private sealed class RangeDownloadHandler(
        byte[] payload,
        int failuresRemaining = 0,
        int disposeFailuresRemaining = 0) : HttpMessageHandler
    {
        private int _failuresRemaining = failuresRemaining;
        private int _disposeFailuresRemaining = disposeFailuresRemaining;

        public int RequestCount { get; private set; }
        public List<string> RequestedRanges { get; } = [];
        public List<CapturedDownloadRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Requests.Add(new CapturedDownloadRequest(
                request.RequestUri?.Host,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var requestedRange = request.Headers.Range?.Ranges.SingleOrDefault();
            var start = requestedRange?.From ?? 0;
            var end = requestedRange?.To ?? payload.LongLength - 1;
            if (requestedRange is not null)
                RequestedRanges.Add($"bytes={start}-{end}");

            var segment = payload[(int)start..((int)end + 1)];
            HttpContent content;
            if (_disposeFailuresRemaining > 0)
            {
                _disposeFailuresRemaining--;
                content = new StreamContent(new DisposeFailingMemoryStream(segment));
                content.Headers.ContentLength = segment.Length;
            }
            else
            {
                content = new ByteArrayContent(segment);
            }

            var response = new HttpResponseMessage(
                requestedRange is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = content
            };
            if (requestedRange is not null)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start,
                    end,
                    payload.LongLength);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class DisposeFailingMemoryStream(byte[] buffer)
        : MemoryStream(buffer, writable: false)
    {
        private bool _shouldFail = true;

        public override async ValueTask DisposeAsync()
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new IOException("Simulated failure after the completed transfer.");
            }

            await base.DisposeAsync();
        }
    }

    private sealed record CapturedDownloadRequest(
        string? Host,
        string? AuthorizationScheme,
        string? AuthorizationParameter);

    private sealed class FakeCrispAsrServer : ICrispAsrServer
    {
        public bool IsRunning { get; private set; }
        public CrispAsrBackend? ActiveBackend { get; private set; }
        public CrispAsrServerConfiguration? LastConfiguration { get; private set; }
        public string? LastLanguage { get; private set; }
        public int StartCount { get; private set; }
        public bool FailNextTranscriptionAndStop { get; set; }

        public Task StartAsync(
            CrispAsrServerConfiguration configuration,
            CancellationToken cancellationToken)
        {
            StartCount++;
            LastConfiguration = configuration;
            ActiveBackend = configuration.Backend;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            CancellationToken cancellationToken)
        {
            LastLanguage = language;
            if (FailNextTranscriptionAndStop)
            {
                FailNextTranscriptionAndStop = false;
                IsRunning = false;
                ActiveBackend = null;
                throw new HttpRequestException("CrispASR stopped during transcription.");
            }

            return Task.FromResult(new PluginTranscriptionResult("lokal", language, 1, null));
        }

        public Task StopAsync()
        {
            IsRunning = false;
            ActiveBackend = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsRunning = false;
            ActiveBackend = null;
        }
    }

    private sealed class FakePluginHostServices : IPluginHostServices
    {
        public FakePluginHostServices(
            string pluginAssetDirectory,
            Dictionary<string, string>? secrets = null)
        {
            PluginDataDirectory = pluginAssetDirectory;
            Secrets = secrets ?? [];
        }

        public Dictionary<string, string> Secrets { get; }
        public List<(PluginLogLevel Level, string Message)> Logs { get; } = [];
        public Exception? StoreSecretException { get; init; }
        public Exception? DeleteSecretException { get; init; }
        public string PluginDataDirectory { get; }
        public string PluginAssetDirectory => PluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new NoOpPluginLocalization();

        public Task StoreSecretAsync(string key, string value)
        {
            if (StoreSecretException is not null)
                throw StoreSecretException;

            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            if (DeleteSecretException is not null)
                throw DeleteSecretException;

            Secrets.Remove(key);
            return Task.CompletedTask;
        }
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) =>
            Logs.Add((level, message));
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string content)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NoOpPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
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
        public string GetString(string key, params object[] args) =>
            string.Format(key, args);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                $"tw-cohere-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
