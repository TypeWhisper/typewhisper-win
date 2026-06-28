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
    [Fact]
    public void Manifest_DeclaresLocalTtsPlugin()
    {
        var manifestPath = FindRepoFile("plugins", "TypeWhisper.Plugin.SupertonicTts", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        Assert.Equal("com.typewhisper.supertonic-tts", root.GetProperty("id").GetString());
        Assert.Equal("Supertonic TTS", root.GetProperty("name").GetString());
        Assert.Equal("1.0.0", root.GetProperty("minHostVersion").GetString());
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
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
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
            await sut.DownloadMissingAssetsAsync(new Progress<double>(p => progressValues.Add(p)), CancellationToken.None);

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
        public bool AreAssetsReady => AreAssetsReadyValue;

        public Task DownloadMissingAssetsAsync(IProgress<double>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DownloadCount++;
            AreAssetsReadyValue = true;
            progress?.Report(1.0);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSupertonicSynthesizer : ISupertonicSynthesizer
    {
        public SupertonicSynthesisRequest? LastRequest { get; private set; }

        public SupertonicSynthesisResult Synthesize(SupertonicSynthesisRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            return new SupertonicSynthesisResult([0.1f, -0.1f], 24_000);
        }

        public void Dispose()
        {
        }
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
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;

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
