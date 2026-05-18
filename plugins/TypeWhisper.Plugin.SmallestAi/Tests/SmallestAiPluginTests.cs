using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.SmallestAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class SmallestAiPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new SmallestAiPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesTranscriptionCapabilitiesAndApiKeyRequirement()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.smallest-ai", manifest.GetProperty("id").GetString());
        Assert.Equal("Smallest AI Pulse", manifest.GetProperty("name").GetString());
        Assert.Equal("transcription", manifest.GetProperty("category").GetString());
        Assert.Equal(["transcription"], manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.True(manifest.GetProperty("requiresApiKey").GetBoolean());
    }

    [Fact]
    public void Localization_ProvidesApiKeyLabelAndGermanDiacritics()
    {
        var en = LoadLocalization("en");
        var de = LoadLocalization("de");

        Assert.Equal("API Key", en.GetProperty("Settings.ApiKeyLabel").GetString());
        Assert.Equal("API-Key", de.GetProperty("Settings.ApiKeyLabel").GetString());
        Assert.Equal("API-Key gültig!", de.GetProperty("Settings.ApiKeyValid").GetString());
        Assert.Equal("Ungültiger API-Key", de.GetProperty("Settings.ApiKeyInvalid").GetString());
    }

    [Fact]
    public async Task ActivateAsync_RestoresApiKeyAndExposesPulseModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "smallest-key";

        var sut = new SmallestAiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.smallest-ai", sut.PluginId);
        Assert.Equal("Smallest AI Pulse", sut.PluginName);
        Assert.Equal("smallest-ai", sut.ProviderId);
        Assert.Equal("Smallest AI", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("pulse", sut.SelectedModelId);
        Assert.Equal(["pulse"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("multi", sut.SupportedLanguages);
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new SmallestAiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" smallest-key ");
        await sut.SetApiKeyAsync("smallest-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    [Fact]
    public async Task SetApiKeyAsync_SerializesConcurrentSecretWrites()
    {
        var host = new TestPluginHostServices
        {
            SecretWriteDelay = TimeSpan.FromMilliseconds(30)
        };
        var sut = new SmallestAiPlugin();
        await sut.ActivateAsync(host);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => sut.SetApiKeyAsync($"smallest-key-{i}")));

        Assert.Equal(1, host.MaxConcurrentSecretWrites);
    }

    [Fact]
    public void SettingsView_FallbackLocalizationIncludesFormatArguments()
    {
        var text = SmallestAiSettingsView.FormatFallbackText("Settings.Error", ["boom"]);

        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_TreatsUnauthorizedAsInvalidAndBadAudioAsAuthenticated()
    {
        var seenStatuses = new Queue<HttpStatusCode>(
            [HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError]);
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.smallest.ai/waves/v1/pulse/get_text", request.RequestUri?.GetLeftPart(UriPartial.Path));
            Assert.Equal("Bearer probe-key", request.Headers.Authorization?.ToString());
            Assert.Equal("audio/wav", request.Content?.Headers.ContentType?.MediaType);
            Assert.NotNull(body);

            return JsonResponse("""{ "error": "probe" }""", seenStatuses.Dequeue());
        });

        using var httpClient = new HttpClient(handler);
        var sut = new SmallestAiPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
        Assert.False(await sut.ValidateApiKeyAsync("probe-key"));
        Assert.False(await sut.ValidateApiKeyAsync("probe-key"));
    }

    [Fact]
    public async Task TranscribeAsync_PostsWavToPulseWithLanguageAndTimestampFlags()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.smallest.ai/waves/v1/pulse/get_text", request.RequestUri?.GetLeftPart(UriPartial.Path));
            Assert.Contains("language=de", request.RequestUri?.Query);
            Assert.Contains("word_timestamps=true", request.RequestUri?.Query);
            Assert.Equal("Bearer smallest-key", request.Headers.Authorization?.ToString());
            Assert.Equal("audio/wav", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal([1, 2, 3], body);

            return JsonResponse("""
                {
                  "status": "success",
                  "transcription": "Hallo Welt",
                  "language": "de",
                  "words": [
                    { "word": "Hallo", "start": 0.0, "end": 0.5, "confidence": 0.98 },
                    { "word": "Welt", "start": 0.5, "end": 1.1, "confidence": 0.97 }
                  ],
                  "utterances": [
                    { "text": "Hallo Welt", "start": 0.0, "end": 1.1 }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "smallest-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SmallestAiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "de", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.1, result.DurationSeconds);
        var segment = Assert.Single(result.Segments);
        Assert.Equal("Hallo Welt", segment.Text);
        Assert.Equal(0.0, segment.Start);
        Assert.Equal(1.1, segment.End);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsLanguageForAuto()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.DoesNotContain("language=", request.RequestUri?.Query);
            return JsonResponse("""{ "status": "success", "transcription": "Hello" }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "smallest-key";
        using var httpClient = new HttpClient(handler);
        var sut = new SmallestAiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "auto", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslation()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "smallest-key";
        var sut = new SmallestAiPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: true, prompt: null, CancellationToken.None));

        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public void ParseTranscriptionResponse_FallsBackToWordSegmentsWhenUtterancesAreMissing()
    {
        var result = SmallestAiPlugin.ParseTranscriptionResponse("""
            {
              "status": "success",
              "transcription": "Hello world",
              "words": [
                { "word": "Hello", "start": 0.25, "end": 0.75 },
                { "word": "world", "start": 0.75, "end": 1.25 }
              ]
            }
            """, "en");

        Assert.Equal("Hello world", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal(["Hello", "world"], result.Segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void StreamingSession_BuildsExpectedUriHeadersAndCollectsTranscriptEvents()
    {
        var uri = SmallestAiStreamingSession.BuildStreamingUri("de", wordTimestamps: true);
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("api.smallest.ai", uri.Host);
        Assert.Equal("/waves/v1/pulse/get_text", uri.AbsolutePath);
        Assert.Contains("language=de", uri.Query);
        Assert.Contains("encoding=linear16", uri.Query);
        Assert.Contains("sample_rate=16000", uri.Query);
        Assert.Contains("word_timestamps=true", uri.Query);

        var autoUri = SmallestAiStreamingSession.BuildStreamingUri("auto", wordTimestamps: true);
        Assert.DoesNotContain("language=", autoUri.Query);

        var headers = SmallestAiStreamingSession.CreateStreamingHeaders("smallest-key");
        Assert.Equal("Bearer smallest-key", headers["Authorization"]);

        var collector = new SmallestAiTranscriptCollector();
        var partial = collector.ApplyEvent("""
            { "type": "transcription", "status": "success", "transcript": "Hel", "is_final": false, "is_last": false }
            """);
        Assert.Equal("Hel", partial?.Text);
        Assert.False(partial?.IsFinal);

        var final = collector.ApplyEvent("""
            { "type": "transcription", "status": "success", "transcript": "Hello", "is_final": true, "is_last": false, "language": "en" }
            """);
        Assert.Equal("Hello", final?.Text);
        Assert.True(final?.IsFinal);

        var last = collector.ApplyEvent("""
            { "type": "transcription", "status": "success", "transcript": "world", "is_final": true, "is_last": true, "language": "en" }
            """);
        Assert.Equal("world", last?.Text);
        Assert.True(last?.IsFinal);
        Assert.Equal("en", collector.DetectedLanguage);
    }

    [Fact]
    public void StreamingCollector_ThrowsActionableMessageForApiErrors()
    {
        var collector = new SmallestAiTranscriptCollector();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            collector.ApplyEvent("""{ "type": "error", "status": "error", "message": "Invalid API key" }"""));

        Assert.Contains("Invalid API key", ex.Message);
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.SmallestAi", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static JsonElement LoadLocalization(string language)
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeLocalizationPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.SmallestAi", "Localization", $"{language}.json");
        var localizationPath = Path.GetFullPath(relativeLocalizationPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(localizationPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, byte[]?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
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
        private readonly object _secretLock = new();
        private int _activeSecretWrites;
        private int _maxConcurrentSecretWrites;
        public Dictionary<string, string?> Secrets { get; } = [];
        public TimeSpan SecretWriteDelay { get; init; }
        public int MaxConcurrentSecretWrites => Volatile.Read(ref _maxConcurrentSecretWrites);
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value) =>
            TrackSecretWriteAsync(() => Secrets[key] = value);

        public Task<string?> LoadSecretAsync(string key)
        {
            lock (_secretLock)
                return Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);
        }

        public Task DeleteSecretAsync(string key) =>
            TrackSecretWriteAsync(() => Secrets.Remove(key));

        private async Task TrackSecretWriteAsync(Action write)
        {
            var active = Interlocked.Increment(ref _activeSecretWrites);
            RecordMaxConcurrentSecretWrites(active);

            try
            {
                if (SecretWriteDelay > TimeSpan.Zero)
                    await Task.Delay(SecretWriteDelay);

                lock (_secretLock)
                    write();
            }
            finally
            {
                Interlocked.Decrement(ref _activeSecretWrites);
            }
        }

        private void RecordMaxConcurrentSecretWrites(int active)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maxConcurrentSecretWrites);
                if (active <= current)
                    return;
            } while (Interlocked.CompareExchange(ref _maxConcurrentSecretWrites, active, current) != current);
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
