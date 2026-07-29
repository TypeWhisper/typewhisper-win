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
    public void ModelMetadata_ExposesPinnedBalancedModelAndFourteenLanguages()
    {
        var sut = new CohereTranscribePlugin();
        var model = Assert.Single(sut.TranscriptionModels);

        Assert.Equal(CohereTranscribePlugin.ModelId, model.Id);
        Assert.True(model.IsRecommended);
        Assert.Equal(14, model.LanguageCount);
        Assert.Equal(14, sut.SupportedLanguages.Count);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("ja", sut.SupportedLanguages);
        Assert.DoesNotContain("ru", sut.SupportedLanguages);
    }

    [Fact]
    public void DownloadDefinitions_AreRevisionPinnedAndSha256Pinned()
    {
        Assert.Contains(
            CohereLocalAssetManager.CohereModelRevision,
            CohereLocalAssetManager.CohereModel.DownloadUrl);
        Assert.Contains(
            CohereLocalAssetManager.VadModelRevision,
            CohereLocalAssetManager.VadModel.DownloadUrl);
        Assert.Contains(
            CohereLocalAssetManager.LanguageIdModelRevision,
            CohereLocalAssetManager.LanguageIdModel.DownloadUrl);

        Assert.All(
            new[]
            {
                CohereLocalAssetManager.CohereModel,
                CohereLocalAssetManager.VadModel,
                CohereLocalAssetManager.LanguageIdModel,
                CohereLocalAssetManager.CpuRuntime.Archive,
                CohereLocalAssetManager.CudaRuntime.Archive,
                CohereLocalAssetManager.VulkanRuntime.Archive
            },
            artifact =>
            {
                Assert.Equal(64, artifact.Sha256.Length);
                Assert.True(artifact.SizeBytes > 0);
                Assert.StartsWith("https://", artifact.DownloadUrl);
            });
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
            @"C:\runtime\crispasr.exe",
            paths,
            CrispAsrBackend.Cuda,
            8);
        const string apiKey = "not-on-the-command-line";

        var startInfo = CrispAsrServer.BuildStartInfo(configuration, 43123, apiKey);
        var arguments = startInfo.ArgumentList.ToArray();

        AssertArgumentPair(arguments, "--host", "127.0.0.1");
        AssertArgumentPair(arguments, "--port", "43123");
        AssertArgumentPair(arguments, "--backend", "cohere");
        AssertArgumentPair(arguments, "--model", paths.ModelPath);
        AssertArgumentPair(arguments, "--lid-backend", "ecapa");
        AssertArgumentPair(arguments, "--lid-model", paths.LanguageIdModelPath);
        AssertArgumentPair(arguments, "--vad-model", paths.VadModelPath);
        AssertArgumentPair(arguments, "--gpu-backend", "cuda");
        Assert.Contains("--strict-pipeline", arguments);
        Assert.Contains("--require-vad", arguments);
        Assert.DoesNotContain("--api-keys", arguments);
        Assert.DoesNotContain(apiKey, arguments);
        Assert.Equal(apiKey, startInfo.Environment["CRISPASR_API_KEYS"]);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, startInfo.WindowStyle);
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
    public async Task PluginLifecycle_UsesManagedCpuRuntimeAndNormalizesLanguage()
    {
        using var temp = new TempDirectory();
        var assets = new FakeAssetManager();
        var server = new FakeCrispAsrServer();
        using var sut = new CohereTranscribePlugin(assets, server);
        var host = new FakePluginHostServices(temp.Path);
        var downloadProgress = new List<double>();

        await sut.ActivateAsync(host);
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await sut.DownloadModelAsync(
            CohereTranscribePlugin.ModelId,
            new InlineProgress<double>(downloadProgress.Add),
            CancellationToken.None);
        await sut.LoadModelAsync(CohereTranscribePlugin.ModelId, CancellationToken.None);
        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de-DE",
            translate: false,
            prompt: "ignored",
            CancellationToken.None);

        Assert.True(assets.ModelInstalled);
        Assert.Equal([CrispAsrBackend.Cpu], assets.EnsuredRuntimes);
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
    public void SettingsView_LabelsTheOptionalTokenFieldForAssistiveTechnology()
    {
        var xaml = TestFile.ReadProjectFile(
            "plugins",
            "TypeWhisper.Plugin.CohereTranscribe",
            "CohereTranscribeSettingsView.xaml");

        Assert.Contains(
            "AutomationProperties.LabeledBy=\"{Binding ElementName=TokenLabel}\"",
            xaml,
            StringComparison.Ordinal);
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

    private static void AssertArgumentPair(
        IReadOnlyList<string> arguments,
        string name,
        string expectedValue)
    {
        var index = arguments.ToList().IndexOf(name);
        Assert.True(index >= 0, $"Expected argument '{name}'.");
        Assert.True(index + 1 < arguments.Count, $"Expected value after '{name}'.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private sealed class FakeAssetManager : ICohereLocalAssetManager
    {
        public long ModelTransferSize => 100;
        public bool ModelInstalled { get; private set; }
        public string? HuggingFaceToken { get; private set; }
        public List<CrispAsrBackend> EnsuredRuntimes { get; } = [];

        public void SetHuggingFaceToken(string? token)
        {
            HuggingFaceToken = token;
        }

        public bool IsModelInstalled() => ModelInstalled;
        public bool IsRuntimeInstalled(CrispAsrBackend backend) =>
            EnsuredRuntimes.Contains(backend);
        public long GetRuntimeTransferSize(CrispAsrBackend backend) => 20;
        public CohereModelPaths GetModelPaths() => new(
            @"C:\models\cohere.gguf",
            @"C:\models\vad.bin",
            @"C:\models\lid.gguf",
            @"C:\models\cache");
        public string GetRuntimeExecutablePath(CrispAsrBackend backend) =>
            @"C:\runtime\crispasr.exe";

        public Task EnsureModelAsync(
            IProgress<ArtifactTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            ModelInstalled = true;
            progress?.Report(new ArtifactTransferProgress(100, 100));
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

        public Task StartAsync(
            CrispAsrServerConfiguration configuration,
            CancellationToken cancellationToken)
        {
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
