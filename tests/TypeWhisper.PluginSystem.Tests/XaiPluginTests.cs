using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Xai;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class XaiPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new XaiPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesXaiCapabilitiesAndApiKeyRequirement()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.xai", manifest.GetProperty("id").GetString());
        Assert.Equal("xAI / Grok", manifest.GetProperty("name").GetString());
        Assert.Equal("transcription", manifest.GetProperty("category").GetString());
        Assert.Equal(["transcription", "llm", "tts"],
            manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.True(manifest.GetProperty("requiresApiKey").GetBoolean());
    }

    [Fact]
    public async Task ActivateAsync_RestoresDefaultsAndExposesAllProviderCapabilities()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";

        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.xai", sut.PluginId);
        Assert.Equal("xAI / Grok", sut.PluginName);
        Assert.Equal("xai", sut.ProviderId);
        Assert.Equal("xAI / Grok", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.IsAvailable);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("grok-stt", sut.SelectedModelId);
        Assert.Equal(["grok-stt"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("grok-4.3", sut.SelectedLlmModelId);
        Assert.Equal(["grok-4.3"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("eve", sut.SelectedVoiceId);
        Assert.Equal(["eve", "ara", "leo", "rex", "sal"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Contains("Eve", sut.SettingsSummary);
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("xai-key");
        await sut.SetApiKeyAsync("xai-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_FiltersAndSortsXaiModelResults()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.x.ai/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());

            return JsonResponse("""
                {
                  "data": [
                    { "id": "grok-stt", "owned_by": "xai" },
                    { "id": "grok-4.3", "owned_by": "xai" },
                    { "id": "grok-imagine-image", "owned_by": "xai" },
                    { "id": "grok-4.20-0309-non-reasoning", "owned_by": "xai" },
                    { "id": "voice-agent", "owned_by": "xai" },
                    { "id": "embedding-model", "owned_by": "xai" }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchLlmModelsAsync();

        Assert.Equal(["grok-4.20-0309-non-reasoning", "grok-4.3"], models.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ProcessAsync_UsesResponsesApiAndSelectedModel()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/responses", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            var root = doc.RootElement;
            Assert.Equal("grok-4.3", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("system", root.GetProperty("input")[0].GetProperty("role").GetString());
            Assert.Equal("user", root.GetProperty("input")[1].GetProperty("role").GetString());

            return JsonResponse("""{ "output_text": "Cleaned transcript" }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("Cleaned transcript", result);
    }

    [Fact]
    public void XaiResponsesClient_ParseResponse_ExtractsNestedOutputText()
    {
        var result = XaiResponsesClient.ParseResponse("""
            {
              "output": [
                { "type": "reasoning", "status": "completed" },
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "Nested " },
                    { "type": "text", "text": "response" }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("Nested response", result);
    }

    [Fact]
    public async Task TranscribeAsync_PostsWavToSttEndpointWithLanguageFormatFields()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/stt", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            Assert.NotNull(body);
            Assert.True(body.Contains("name=\"format\"", StringComparison.Ordinal)
                || body.Contains("name=format", StringComparison.Ordinal));
            Assert.Contains("true", body);
            Assert.True(body.Contains("name=\"language\"", StringComparison.Ordinal)
                || body.Contains("name=language", StringComparison.Ordinal));
            Assert.Contains("de", body);
            Assert.True(body.Contains("name=\"file\"; filename=\"audio.wav\"", StringComparison.Ordinal)
                || body.Contains("name=file; filename=audio.wav", StringComparison.Ordinal));

            return JsonResponse("""
                {
                  "text": "Hallo Welt",
                  "language": "German",
                  "duration": 1.25,
                  "words": [
                    { "text": "Hallo", "start": 0.0, "end": 0.5 },
                    { "text": "Welt", "start": 0.5, "end": 1.25 }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "de", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("German", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal(["Hallo", "Welt"], result.Segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslation()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";
        var sut = new XaiPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: true, prompt: null, CancellationToken.None));

        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public void StreamingSession_BuildsExpectedUriAndCollectsTranscriptEvents()
    {
        var uri = XaiStreamingSession.BuildStreamingUri("de", interimResults: true);
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("api.x.ai", uri.Host);
        Assert.Equal("/v1/stt", uri.AbsolutePath);
        Assert.Contains("sample_rate=16000", uri.Query);
        Assert.Contains("encoding=pcm", uri.Query);
        Assert.Contains("interim_results=true", uri.Query);
        Assert.Contains("language=de", uri.Query);

        var headers = XaiStreamingSession.CreateStreamingHeaders("xai-key");
        Assert.Equal("Bearer xai-key", headers["Authorization"]);

        var collector = new XaiTranscriptCollector();
        Assert.Null(collector.ApplyEvent("""{"type":"transcript.created"}"""));
        Assert.Equal("hello", collector.ApplyEvent("""{"type":"transcript.partial","text":"hello","is_final":false,"speech_final":false}""")?.Text);
        var final = collector.ApplyEvent("""{"type":"transcript.partial","text":"hello world","is_final":true,"speech_final":true}""");
        Assert.NotNull(final);
        Assert.Equal("hello world", final!.Text);
        Assert.True(final.IsFinal);
        var done = collector.ApplyEvent("""{"type":"transcript.done","text":"hello world","language":"en","duration":1.25}""");
        Assert.Equal("hello world", done?.Text);
        Assert.True(done?.IsFinal);
        Assert.Equal("hello world", collector.FinalResult("en").Text);
    }

    [Fact]
    public async Task FetchVoicesAsync_ParsesVoiceListAndPersistsSelectedVoice()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.x.ai/v1/tts/voices", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {
                  "voices": [
                    { "voice_id": "rex", "name": "Rex", "language": "multilingual" },
                    { "voice_id": "ara", "name": "Ara", "language": "multilingual" }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new XaiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var voices = await sut.FetchVoicesAsync();
        sut.SetFetchedVoices(voices);
        sut.SelectVoice("rex");
        sut.SetCustomVoiceId("custom-voice");

        Assert.Equal(["ara", "rex"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal("custom-voice", sut.SelectedVoiceId);
        Assert.Equal("rex", host.GetSetting<string>("selectedVoice"));
        Assert.Equal("custom-voice", host.GetSetting<string>("customVoiceId"));
    }

    [Fact]
    public async Task SpeakAsync_PostsPcmTtsRequestAndUsesPlaybackFactory()
    {
        byte[]? playbackBytes = null;
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/tts", request.RequestUri?.ToString());
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            var root = doc.RootElement;
            Assert.Equal("Read this", root.GetProperty("text").GetString());
            Assert.Equal("rex", root.GetProperty("voice_id").GetString());
            Assert.Equal("de", root.GetProperty("language").GetString());
            Assert.Equal("pcm", root.GetProperty("output_format").GetProperty("codec").GetString());
            Assert.Equal(24000, root.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            Assert.Equal(1, root.GetProperty("optimize_streaming_latency").GetInt32());
            Assert.True(root.GetProperty("text_normalization").GetBoolean());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 1, 2, 3])
            };
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "xai-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new XaiPlugin(httpClient, pcm =>
        {
            playbackBytes = pcm;
            return new FakeTtsPlaybackSession();
        });
        await sut.ActivateAsync(host);
        sut.SelectVoice("rex");
        sut.SetTtsLowLatency(true);
        sut.SetTtsTextNormalization(true);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Read this", "de"), CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal([0, 1, 2, 3], playbackBytes);
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Xai", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);

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

    private sealed class FakeTtsPlaybackSession : ITtsPlaybackSession
    {
        public bool IsActive => false;

        public event EventHandler? Completed
        {
            add { value?.Invoke(this, EventArgs.Empty); }
            remove { }
        }

        public void Stop() { }
    }
}
