using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class ElevenLabsPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.ElevenLabs", "manifest.json"));
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new ElevenLabsPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task ActivateAsync_UsesScribeV2AsDefaultModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "eleven-key";

        var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.elevenlabs", sut.PluginId);
        Assert.Equal("elevenlabs", sut.ProviderId);
        Assert.Equal(ElevenLabsPlugin.DefaultModelId, sut.SelectedModelId);
        Assert.Equal([ElevenLabsPlugin.DefaultModelId], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.Contains("de", sut.SupportedLanguages);
    }

    [Fact]
    public async Task ActivateAsync_NormalizesStaleSelectedModel()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("selectedModel", "scribe_v1");

        var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(ElevenLabsPlugin.DefaultModelId, sut.SelectedModelId);
    }

    [Fact]
    public async Task SelectModel_PersistsSelectedModel()
    {
        var host = new TestPluginHostServices();
        var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        sut.SelectModel(ElevenLabsPlugin.DefaultModelId);

        Assert.Equal(ElevenLabsPlugin.DefaultModelId, host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" eleven-key ");
        Assert.Equal("eleven-key", host.Secrets["api-key"]);

        await sut.SetApiKeyAsync("");

        Assert.False(host.Secrets.ContainsKey("api-key"));
        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(sut.IsConfigured);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesUserEndpointAndXiApiKeyHeader()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.elevenlabs.io/v1/user", request.RequestUri?.ToString());
            Assert.True(request.Headers.TryGetValues("xi-api-key", out var values));
            Assert.Equal("eleven-key", Assert.Single(values));
            return JsonResponse("""{"user_id":"u"}""");
        });

        using var httpClient = new HttpClient(handler);
        var sut = new ElevenLabsPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("eleven-key"));
    }

    [Fact]
    public async Task TranscribeAsync_SendsMultipartRequestAndParsesResponse()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", request.RequestUri?.ToString());
            Assert.True(request.Headers.TryGetValues("xi-api-key", out var values));
            Assert.Equal("eleven-key", Assert.Single(values));
            Assert.StartsWith("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);

            Assert.NotNull(body);
            Assert.Contains("model_id", body);
            Assert.Contains("scribe_v2", body);
            Assert.Contains("language_code", body);
            Assert.Contains("de", body);
            Assert.Contains("keyterms", body);
            Assert.Contains("TypeWhisper", body);
            Assert.Contains("ElevenLabs", body);
            Assert.DoesNotContain("bad<term", body);

            return JsonResponse("""
                {
                  "language_code": "de",
                  "text": "Hallo Welt",
                  "words": [
                    { "text": "Hallo", "start": 0.1, "end": 0.5, "type": "word" },
                    { "text": " ", "start": 0.5, "end": 0.6, "type": "spacing" },
                    { "text": "Welt", "start": 0.6, "end": 1.0, "type": "word" }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "eleven-key";

        using var httpClient = new HttpClient(handler);
        var sut = new ElevenLabsPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            "TypeWhisper, TypeWhisper, bad<term, ElevenLabs",
            CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.0, result.DurationSeconds);
        Assert.Equal(["Hallo", "Welt"], result.Segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void ExtractKeyterms_FiltersUnsupportedTerms()
    {
        var terms = ElevenLabsPlugin.ExtractKeyterms(
            "TypeWhisper, TypeWhisper, too many words in this single keyterm, bad<term, ElevenLabs");

        Assert.Equal(["TypeWhisper", "ElevenLabs"], terms);
    }

    [Fact]
    public void BuildRealtimeUri_UsesScribeRealtimeAndVad()
    {
        var uri = ElevenLabsStreamingSession.BuildRealtimeUri("scribe_v2_realtime", "de").AbsoluteUri;

        Assert.StartsWith("wss://api.elevenlabs.io/v1/speech-to-text/realtime?", uri);
        Assert.Contains("model_id=scribe_v2_realtime", uri);
        Assert.Contains("audio_format=pcm_16000", uri);
        Assert.Contains("commit_strategy=vad", uri);
        Assert.Contains("include_timestamps=true", uri);
        Assert.Contains("include_language_detection=true", uri);
        Assert.Contains("language_code=de", uri);
    }

    [Fact]
    public void BuildAudioChunkPayload_EncodesAudioAndCommitFlag()
    {
        var json = ElevenLabsStreamingSession.BuildAudioChunkPayload([1, 2, 3], commit: true);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("input_audio_chunk", doc.RootElement.GetProperty("message_type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), doc.RootElement.GetProperty("audio_base_64").GetString());
        Assert.Equal(16000, doc.RootElement.GetProperty("sample_rate").GetInt32());
        Assert.True(doc.RootElement.GetProperty("commit").GetBoolean());
    }

    [Fact]
    public void TryParseTranscriptEvent_ParsesPartialCommittedAndErrorMessages()
    {
        var parsedPartial = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            """{"message_type":"partial_transcript","text":"Hello"}""",
            out var partial,
            out var partialError);
        var parsedFinal = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            """{"message_type":"committed_transcript_with_timestamps","text":"Hello world","language_code":"en"}""",
            out var final,
            out var finalError);
        var parsedError = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            """{"message_type":"scribe_auth_error","message":"Invalid API key"}""",
            out var errorEvent,
            out var error);

        Assert.True(parsedPartial);
        Assert.Equal(new StreamingTranscriptEvent("Hello", false), partial);
        Assert.Null(partialError);

        Assert.True(parsedFinal);
        Assert.Equal(new StreamingTranscriptEvent("Hello world", true), final);
        Assert.Null(finalError);

        Assert.False(parsedError);
        Assert.Null(errorEvent);
        Assert.Equal("Invalid API key", error);
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
}
