using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.SupertonicTts;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class SupertonicTtsPluginTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("crypto")]
    public async Task OptionalSecretFailureDoesNotDisableDownloadedModels(string kind)
    {
        var host = new TestPluginHostServices { SecretReadError = kind switch
        {
            "io" => new IOException(), "access" => new UnauthorizedAccessException(),
            _ => new System.Security.Cryptography.CryptographicException()
        } };
        using var plugin = new SupertonicTtsPlugin(new FakeSupertonicAssets { AreAssetsReadyValue = true }, _ => new FakeSupertonicSynthesizer());
        await plugin.ActivateAsync(host);
        Assert.True(plugin.AreAssetsReady);
    }

    [Fact]
    public void Manifest_DeclaresLocalTtsPlugin()
    {
        var manifestPath = FindRepoFile("plugins-v2", "TypeWhisper.Plugin.SupertonicTts", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        Assert.Equal("com.typewhisper.supertonic-tts", root.GetProperty("id").GetString());
        Assert.Equal("Supertonic TTS", root.GetProperty("name").GetString());
        Assert.Equal("1.1.4", root.GetProperty("minHostVersion").GetString());
        Assert.Equal("tts", root.GetProperty("category").GetString());
        Assert.Contains("tts", root.GetProperty("categories").EnumerateArray().Select(x => x.GetString()));
        Assert.True(root.GetProperty("isLocal").GetBoolean());
        Assert.Equal("TypeWhisper.Plugin.SupertonicTts.dll", root.GetProperty("assemblyName").GetString());
        Assert.Equal("TypeWhisper.Plugin.SupertonicTts.SupertonicTtsPlugin", root.GetProperty("pluginClass").GetString());
    }

    [Fact]
    public async Task ActivateAsync_NormalizesPersistedSettingsAndExposesProviderDefaults()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var host = new TestPluginHostServices();
        host.SetSetting("selectedVoice", "unknown");
        host.SetSetting("speed", 9.0);
        host.SetSetting("denoisingSteps", 0);
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());

        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.supertonic-tts", sut.PluginId);
        Assert.Equal("supertonic-tts", sut.ProviderId);
        Assert.Equal("Supertonic TTS", sut.ProviderDisplayName);
        Assert.False(sut.IsConfigured);
        Assert.Equal("M1", sut.SelectedVoiceId);
        Assert.Equal(1.5, sut.Speed);
        Assert.Equal(1, sut.DenoisingSteps);
        Assert.Equal(10, sut.AvailableVoices.Count);
        Assert.Contains(sut.AvailableVoices, voice => voice.Id == "F5");
    }

    [Fact]
    public async Task DownloadAssetsAsync_RequiresLicenseConfirmationBeforeDownload()
    {
        var assets = new FakeSupertonicAssets();
        var host = new TestPluginHostServices();
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DownloadAssetsAsync(null, CancellationToken.None));

        sut.SetLicenseAccepted(true);
        await sut.DownloadAssetsAsync(null, CancellationToken.None);

        Assert.True(sut.HasAcceptedModelLicense);
        Assert.Equal(1, assets.DownloadCount);
        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task DownloadRequirements_MigrateLegacyLicenseAndRejectStaleRevision()
    {
        var migratedHost = new TestPluginHostServices();
        migratedHost.SetSetting(SupertonicTtsPlugin.LicenseAcceptedSettingName, true);
        var migrated = new SupertonicTtsPlugin(
            new FakeSupertonicAssets(),
            _ => new FakeSupertonicSynthesizer());

        await migrated.ActivateAsync(migratedHost);

        var migratedLicense = Assert.Single(
            migrated.ModelDownloadRequirements,
            requirement => requirement.Kind == PluginModelDownloadRequirementKind.License);
        Assert.True(migratedLicense.IsRequired);
        Assert.True(migratedLicense.IsSatisfied);
        Assert.Equal(
            SupertonicTtsPlugin.ModelLicenseId,
            migratedHost.GetSetting<string>(SupertonicTtsPlugin.AcceptedModelLicenseIdSettingName));
        Assert.Equal(
            SupertonicTtsPlugin.ModelLicenseRevision,
            migratedHost.GetSetting<string>(SupertonicTtsPlugin.AcceptedModelLicenseRevisionSettingName));
        Assert.False(string.IsNullOrWhiteSpace(
            migratedHost.GetSetting<string>(SupertonicTtsPlugin.AcceptedModelLicenseAtSettingName)));
        Assert.Null(migratedHost.GetSetting<bool?>(SupertonicTtsPlugin.LicenseAcceptedSettingName));

        migratedHost.SetSetting(
            SupertonicTtsPlugin.AcceptedModelLicenseRevisionSettingName,
            "previous-revision");
        var remigrated = new SupertonicTtsPlugin(
            new FakeSupertonicAssets(),
            _ => new FakeSupertonicSynthesizer());
        await remigrated.ActivateAsync(migratedHost);

        Assert.False(remigrated.HasAcceptedModelLicense);

        var staleHost = new TestPluginHostServices();
        staleHost.SetSetting(
            SupertonicTtsPlugin.AcceptedModelLicenseIdSettingName,
            SupertonicTtsPlugin.ModelLicenseId);
        staleHost.SetSetting(
            SupertonicTtsPlugin.AcceptedModelLicenseRevisionSettingName,
            "previous-revision");
        var stale = new SupertonicTtsPlugin(
            new FakeSupertonicAssets(),
            _ => new FakeSupertonicSynthesizer());

        await stale.ActivateAsync(staleHost);

        Assert.False(Assert.Single(
            stale.ModelDownloadRequirements,
            requirement => requirement.Kind == PluginModelDownloadRequirementKind.License).IsSatisfied);
    }

    [Fact]
    public async Task DownloadRequirements_PersistTokenAndPassItToAssetDownload()
    {
        var assets = new FakeSupertonicAssets();
        var host = new TestPluginHostServices();
        using var validationClient = new HttpClient(new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"name\":\"typewhisper\"}", Encoding.UTF8, "application/json")
            }));
        var sut = new SupertonicTtsPlugin(
            assets,
            _ => new FakeSupertonicSynthesizer(),
            huggingFaceTokenValidationClient: validationClient);
        await sut.ActivateAsync(host);

        var saved = await sut.SaveModelDownloadCredentialAsync(
            SupertonicTtsPlugin.ModelId,
            SupertonicTtsPlugin.HuggingFaceTokenRequirementId,
            "  hf_valid  ",
            CancellationToken.None);
        await sut.SetModelDownloadLicenseAcceptanceAsync(
            SupertonicTtsPlugin.ModelId,
            SupertonicTtsPlugin.ModelLicenseRequirementId,
            accepted: true,
            CancellationToken.None);
        await sut.DownloadAssetsAsync(null, CancellationToken.None);

        Assert.True(saved.Succeeded);
        Assert.Equal("hf_valid", host.Secrets["hugging-face-token"]);
        Assert.Equal("hf_valid", assets.LastHuggingFaceToken);
        Assert.All(sut.ModelDownloadRequirements, requirement => Assert.True(requirement.IsSatisfied));

        await sut.ClearModelDownloadCredentialAsync(
            SupertonicTtsPlugin.ModelId,
            SupertonicTtsPlugin.HuggingFaceTokenRequirementId,
            CancellationToken.None);

        Assert.DoesNotContain("hugging-face-token", host.Secrets);
        Assert.False(Assert.Single(
            sut.ModelDownloadRequirements,
            requirement => requirement.Kind == PluginModelDownloadRequirementKind.Credential).IsSatisfied);
    }

    [Fact]
    public async Task SpeakAsync_EmptyTextReturnsInactiveSessionAndMissingAssetsThrow()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());

        var empty = await sut.SpeakAsync(new TtsSpeakRequest("   ", "en"), CancellationToken.None);
        Assert.False(empty.IsActive);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello", "en"), CancellationToken.None));
        Assert.Contains("Supertonic 3 assets", ex.Message);
    }

    [Fact]
    public async Task SpeakAsync_UsesSynthesizerWithSelectedVoiceLanguageAndSettings()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true, AssetRoot = @"C:\models\supertonic-3" };
        var synth = new FakeSupertonicSynthesizer();
        float[]? playedSamples = null;
        int? playedSampleRate = null;
        var sut = new SupertonicTtsPlugin(
            assets,
            _ => synth,
            (samples, sampleRate) =>
            {
                playedSamples = samples;
                playedSampleRate = sampleRate;
                return new FakeTtsPlaybackSession();
            });
        await sut.ActivateAsync(new TestPluginHostServices());
        sut.SelectVoice("F3");
        sut.SetSpeed(1.25);
        sut.SetDenoisingSteps(12);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Hallo Welt", "de-DE"), CancellationToken.None);

        Assert.True(session.IsActive);
        Assert.Equal("Hallo Welt", synth.LastRequest?.Text);
        Assert.Equal("de", synth.LastRequest?.Language);
        Assert.EndsWith(Path.Combine("voice_styles", "F3.json"), synth.LastRequest?.VoiceStylePath);
        Assert.Equal(1.25, synth.LastRequest?.Speed);
        Assert.Equal(12, synth.LastRequest?.DenoisingSteps);
        Assert.NotNull(playedSamples);
        Assert.Equal([0.1f, -0.1f], playedSamples);
        Assert.Equal(24_000, playedSampleRate);
    }

    [Fact]
    public async Task AssetManager_DownloadsMissingFilesAtomicallyAndWritesSourceMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var calls = new List<string>();
        var handler = new CapturingHandler(request =>
        {
            calls.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("payload"))
            };
        });
        var files = new[]
        {
            new SupertonicAssetFile("onnx/a.onnx", "https://example.test/a.onnx", 1),
            new SupertonicAssetFile("voice_styles/M1.json", "https://example.test/M1.json", 1),
        };
        using var httpClient = new HttpClient(handler);
        var sut = new SupertonicAssetManager(tempDir, httpClient, files, "https://example.test/LICENSE");
        var progressValues = new List<double>();

        try
        {
            await sut.DownloadMissingAssetsAsync(
                new Progress<double>(p => progressValues.Add(p)),
                huggingFaceToken: null,
                CancellationToken.None);

            Assert.True(sut.AreAssetsReady);
            Assert.Equal(3, calls.Count);
            Assert.True(File.Exists(Path.Combine(tempDir, "onnx", "a.onnx")));
            Assert.True(File.Exists(Path.Combine(tempDir, "voice_styles", "M1.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, "onnx", "a.onnx.tmp")));
            Assert.Contains("https://example.test/LICENSE", File.ReadAllText(Path.Combine(tempDir, "SOURCE.txt")));
            Assert.Equal(1.0, progressValues.Last(), precision: 3);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AssetManager_SendsTokenOnlyToHuggingFace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var requests = new List<(string? Host, string? Authorization)>();
        var handler = new CapturingHandler(request =>
        {
            requests.Add((request.RequestUri?.Host, request.Headers.Authorization?.Parameter));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("payload"))
            };
        });
        var files = new[]
        {
            new SupertonicAssetFile(
                "onnx/hugging-face.onnx",
                "https://huggingface.co/example/model.onnx",
                1),
            new SupertonicAssetFile(
                "onnx/external.onnx",
                "https://example.test/model.onnx",
                1),
        };
        using var httpClient = new HttpClient(handler);
        var sut = new SupertonicAssetManager(
            tempDir,
            httpClient,
            files,
            "https://huggingface.co/example/LICENSE");

        try
        {
            await sut.DownloadMissingAssetsAsync(null, "hf_private", CancellationToken.None);

            Assert.Contains(requests, request =>
                request.Host == "huggingface.co" && request.Authorization == "hf_private");
            Assert.Contains(requests, request =>
                request.Host == "example.test" && request.Authorization is null);
            Assert.DoesNotContain(requests, request =>
                request.Host != "huggingface.co" && request.Authorization is not null);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalModel_LoadUnloadAndImmediateLicensePersistence()
    {
        var assets = new FakeSupertonicAssets();
        var host = new TestPluginHostServices();
        var synth = new FakeSupertonicSynthesizer();
        using var plugin = new SupertonicTtsPlugin(assets, _ => synth);
        await plugin.ActivateAsync(host);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.DownloadAndLoadModelAsync(null, default));
        await plugin.SetModelDownloadLicenseAcceptanceAsync("supertonic-3", "model-license", true, default);
        Assert.Equal(SupertonicTtsPlugin.ModelLicenseRevision, host.GetSetting<string>(SupertonicTtsPlugin.AcceptedModelLicenseRevisionSettingName));
        Assert.DoesNotContain(plugin.TextSettings, f => f.Id == "license");
        var progress = new List<double>();
        await plugin.DownloadAndLoadModelAsync(new InlineProgress(progress.Add), default);
        Assert.True(plugin.IsModelDownloaded);
        Assert.True(plugin.IsModelLoaded);
        Assert.Equal(1, progress[^1]);
        await plugin.UnloadModelAsync(default);
        Assert.True(synth.Disposed);
        Assert.False(plugin.IsModelLoaded);
        Assert.True(plugin.IsModelDownloaded);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task LocalModel_CancellationDuringNativeLoadDisposesCandidate()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true };
        var synth = new FakeSupertonicSynthesizer();
        using var cancellation = new CancellationTokenSource();
        using var plugin = new SupertonicTtsPlugin(assets, _ => { cancellation.Cancel(); return synth; });
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.SetModelDownloadLicenseAcceptanceAsync("supertonic-3", "model-license", true, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.DownloadAndLoadModelAsync(null, cancellation.Token));
        Assert.True(synth.Disposed);
        Assert.False(plugin.IsModelLoaded);
        await plugin.UnloadModelAsync(default); // The operation released its gate.
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetManager_RejectsUnverifiedFileAndCleansTemporaryDownload(bool wrongSize)
    {
        var root = Path.Combine(Path.GetTempPath(), "supertonic-verification-" + Guid.NewGuid().ToString("N"));
        var payload = Encoding.UTF8.GetBytes("verified model");
        using var http = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        using var assets = new SupertonicAssetManager(root, http,
            [new("model.onnx", "https://fixture.invalid/model", wrongSize ? payload.Length + 1 : payload.Length, wrongSize ? hash : new string('0', 64))], "https://fixture.invalid/license");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => assets.DownloadMissingAssetsAsync(null, null, default));
            Assert.False(assets.AreAssetsReady);
            Assert.False(File.Exists(Path.Combine(root, "model.onnx")));
            Assert.False(File.Exists(Path.Combine(root, "model.onnx.tmp")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetManager_ReportsCompletionOnlyAfterMetadataIsReady(bool emptyLicense)
    {
        var root = Path.Combine(Path.GetTempPath(), "supertonic-progress-" + Guid.NewGuid().ToString("N"));
        var payload = Encoding.UTF8.GetBytes("verified model");
        using var http = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        using var assets = new SupertonicAssetManager(root, http,
            [new("model.onnx", "https://fixture.invalid/model", payload.Length, hash)], "https://fixture.invalid/license");
        var updates = new List<(double Value, bool Ready)>();
        try
        {
            if (emptyLicense)
            { Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, SupertonicPaths.LicenseFileName), ""); }
            await assets.DownloadMissingAssetsAsync(new InlineProgress(v => updates.Add((v, assets.AreAssetsReady))), null, default);
            Assert.True(assets.AreAssetsReady);
            Assert.Equal((1d, true), updates[^1]);
            Assert.All(updates.Where(u => !u.Ready), u => Assert.True(u.Value < 1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PortableSpeechSettings_PersistVoiceSpeedAndQuality()
    {
        var host = new TestPluginHostServices();
        using var plugin = new SupertonicTtsPlugin(new FakeSupertonicAssets(), _ => new FakeSupertonicSynthesizer());
        await plugin.ActivateAsync(host);
        await plugin.SaveTextSettingAsync("voice", "F3", default);
        await plugin.SaveTextSettingAsync("speed", "1.2", default);
        await plugin.SaveTextSettingAsync("steps", "16", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("voice", "missing", default));
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Equal("F3", plugin.SelectedVoiceId);
        Assert.Equal(1.2, plugin.Speed);
        Assert.Equal(16, plugin.DenoisingSteps);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SpeakAsync_LeavesCallerSynchronizationContextDuringInference()
    {
        var synth = new FakeSupertonicSynthesizer();
        using var plugin = new SupertonicTtsPlugin(new FakeSupertonicAssets { AreAssetsReadyValue = true }, _ => synth,
            (_, _) => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(new TestPluginHostServices());
        var previous = SynchronizationContext.Current;
        Task<ITtsPlaybackSession> pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            pending = plugin.SpeakAsync(new TtsSpeakRequest("Hello", "en"), default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await pending;
        Assert.Null(synth.SynthesisContext);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(300)]
    public void TextChunksKeepSupplementaryCharactersIntact(int limit)
    {
        var text = new string('a', limit - 1) + "\U0001F600" + "tail";
        var chunks = SupertonicOnnxSynthesizer.ChunkText(text, limit);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.NotNull(chunk.Normalize(NormalizationForm.FormKD)));
    }

    [Fact]
    public async Task SpeakAsync_RejectsExplicitUnknownVoiceBeforeSynthesis()
    {
        var synth = new FakeSupertonicSynthesizer();
        using var plugin = new SupertonicTtsPlugin(new FakeSupertonicAssets { AreAssetsReadyValue = true }, _ => synth);
        await plugin.ActivateAsync(new TestPluginHostServices());
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SpeakAsync(new TtsSpeakRequest("Hello", "en") { VoiceId = "missing" }, default));
        Assert.Null(synth.LastRequest);
    }

    [Theory]
    [InlineData(120, 1, true)]
    [InlineData(121, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(6291457, 96000, false)]
    [InlineData(5292000, 44100, true)]
    public async Task SpeakAsync_ChecksAudioBoundsBeforePlayback(int samples, int rate, bool valid)
    {
        var synth = new FakeSupertonicSynthesizer { Result = new(new float[samples], rate) };
        var played = false;
        using var plugin = new SupertonicTtsPlugin(new FakeSupertonicAssets { AreAssetsReadyValue = true }, _ => synth,
            (_, _) => { played = true; return new FakeTtsPlaybackSession(); });
        await plugin.ActivateAsync(new TestPluginHostServices());
        var speak = () => plugin.SpeakAsync(new TtsSpeakRequest("Hello", "en"), default);
        if (valid) await speak(); else await Assert.ThrowsAsync<InvalidOperationException>(speak);
        Assert.Equal(valid, played);
    }

    [Fact]
    public async Task AssetManager_ReplacesCorruptedSameLengthFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "supertonic-repair-" + Guid.NewGuid().ToString("N"));
        var payload = Encoding.UTF8.GetBytes("good");
        using var http = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        using var assets = new SupertonicAssetManager(root, http, [new("model.onnx", "https://fixture.invalid/model", payload.Length, hash)], "https://fixture.invalid/license");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "model.onnx"), "bad!");
            await assets.DownloadMissingAssetsAsync(null, null, default);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(root, "model.onnx")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)}");
    }

    private sealed class FakeSupertonicAssets : ISupertonicAssetManager
    {
        public string AssetRoot { get; set; } = Path.GetTempPath();
        public bool AreAssetsReadyValue { get; set; }
        public int DownloadCount { get; private set; }
        public string? LastHuggingFaceToken { get; private set; }
        public bool AreAssetsReady => AreAssetsReadyValue;

        public Task DownloadMissingAssetsAsync(
            IProgress<double>? progress,
            string? huggingFaceToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DownloadCount++;
            LastHuggingFaceToken = huggingFaceToken;
            AreAssetsReadyValue = true;
            progress?.Report(1.0);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSupertonicSynthesizer : ISupertonicSynthesizer
    {
        public SupertonicSynthesisRequest? LastRequest { get; private set; }
        public SynchronizationContext? SynthesisContext { get; private set; }
        public bool Disposed { get; private set; }
        public SupertonicSynthesisResult Result { get; set; } = new([0.1f, -0.1f], 24_000);

        public SupertonicSynthesisResult Synthesize(SupertonicSynthesisRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            SynthesisContext = SynchronizationContext.Current;
            return Result;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string> Secrets { get; } = [];
        public Exception? SecretReadError { get; set; }
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) => SecretReadError is { } error
            ? Task.FromException<string?>(error) : Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
