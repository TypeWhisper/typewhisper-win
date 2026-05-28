using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class Reson8PluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new Reson8Plugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesTranscriptionCapabilitiesAndApiKeyRequirement()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.reson8", manifest.GetProperty("id").GetString());
        Assert.Equal("Reson8", manifest.GetProperty("name").GetString());
        Assert.Equal("transcription", manifest.GetProperty("category").GetString());
        Assert.Equal(["transcription"], manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(manifest.GetProperty("isLocal").GetBoolean());
        Assert.True(manifest.GetProperty("requiresApiKey").GetBoolean());
    }

    [Fact]
    public void Localization_ProvidesSettingsLabelsAndGermanDiacritics()
    {
        var en = LoadLocalization("en");
        var de = LoadLocalization("de");

        Assert.Equal("API Key", en.GetProperty("Settings.ApiKeyLabel").GetString());
        Assert.Equal("API-Schlüssel", de.GetProperty("Settings.ApiKeyLabel").GetString());
        Assert.Equal("API-Schlüssel gültig!", de.GetProperty("Settings.ApiKeyValid").GetString());
        Assert.Equal("Ungültiger API-Schlüssel", de.GetProperty("Settings.ApiKeyInvalid").GetString());
    }

    [Fact]
    public async Task ActivateAsync_RestoresApiKeySettingsAndCustomModels()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        host.SetSetting("selectedModel", "domain-model");
        host.SetSetting("customBaseURL", "https://proxy.example.test/");
        host.SetSetting("customAuthHeader", "X-Api-Key");
        host.SetSetting("fetchedCustomModels", new[]
        {
            new Reson8CustomModel("domain-model", "Domain Model", "Support vocabulary", 42)
        });

        var sut = new Reson8Plugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.reson8", sut.PluginId);
        Assert.Equal("reson8", sut.ProviderId);
        Assert.Equal("Reson8", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("domain-model", sut.SelectedModelId);
        Assert.Equal(["__default__", "domain-model"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("https://proxy.example.test", sut.CustomBaseUrl);
        Assert.Equal("X-Api-Key", sut.CustomAuthHeader);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.DoesNotContain("ja", sut.SupportedLanguages);
    }

    [Fact]
    public async Task SelectModel_PersistsSelectedModel()
    {
        var host = new TestPluginHostServices();
        var sut = new Reson8Plugin();
        await sut.ActivateAsync(host);

        sut.SetFetchedCustomModels([new Reson8CustomModel("domain-model", "Domain Model", null, null)]);
        sut.SelectModel("domain-model");

        Assert.Equal("domain-model", sut.SelectedModelId);
        Assert.Equal("domain-model", host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new Reson8Plugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" reson-key ");
        await sut.SetApiKeyAsync("reson-key");
        await sut.SetApiKeyAsync("");

        Assert.False(host.Secrets.ContainsKey("api-key"));
        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task AdvancedSettings_AreNormalizedAndPersisted()
    {
        var host = new TestPluginHostServices();
        var sut = new Reson8Plugin();
        await sut.ActivateAsync(host);

        sut.SetCustomBaseUrl(" https://proxy.example.test/// ");
        sut.SetCustomAuthHeader(" X-Api-Key ");

        Assert.Equal("https://proxy.example.test", sut.CustomBaseUrl);
        Assert.Equal("X-Api-Key", sut.CustomAuthHeader);
        Assert.Equal("https://proxy.example.test", host.GetSetting<string>("customBaseURL"));
        Assert.Equal("X-Api-Key", host.GetSetting<string>("customAuthHeader"));
    }

    [Fact]
    public async Task ValidateApiKeyAsync_TreatsBadAudioAsAuthenticatedAndUnauthorizedAsInvalid()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized]);
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.reson8.dev/v1/speech-to-text/prerecorded", request.RequestUri?.GetLeftPart(UriPartial.Path));
            Assert.Equal("ApiKey probe-key", request.Headers.Authorization?.ToString());
            Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);
            Assert.Empty(body ?? []);

            return JsonResponse("""{ "message": "probe" }""", statuses.Dequeue());
        });

        using var httpClient = new HttpClient(handler);
        var sut = new Reson8Plugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
        Assert.False(await sut.ValidateApiKeyAsync("probe-key"));
    }

    [Fact]
    public async Task ValidateApiKeyAsync_MatchesMacBehaviorAndOnlyTreatsUnauthorizedAsInvalid()
    {
        var statuses = new Queue<HttpStatusCode>([
            HttpStatusCode.InternalServerError,
            HttpStatusCode.MethodNotAllowed,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Unauthorized
        ]);
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "message": "probe" }""", statuses.Dequeue()));

        using var httpClient = new HttpClient(handler);
        var sut = new Reson8Plugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
        Assert.True(await sut.ValidateApiKeyAsync("probe-key"));
        Assert.False(await sut.ValidateApiKeyAsync("probe-key"));
    }

    [Fact]
    public async Task TranscribeAsync_PostsPcm16ToPrerecordedEndpointWithLanguageAndCustomModel()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.reson8.dev/v1/speech-to-text/prerecorded", request.RequestUri?.GetLeftPart(UriPartial.Path));
            Assert.Contains("encoding=pcm_s16le", request.RequestUri?.Query);
            Assert.Contains("sample_rate=16000", request.RequestUri?.Query);
            Assert.Contains("channels=1", request.RequestUri?.Query);
            Assert.Contains("language=de", request.RequestUri?.Query);
            Assert.Contains("custom_model_id=domain-model", request.RequestUri?.Query);
            Assert.Equal("ApiKey reson-key", request.Headers.Authorization?.ToString());
            Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal([0x01, 0x00, 0xFF, 0xFF], body);

            return JsonResponse("""{ "text": "Hallo Welt", "language": "de" }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        host.SetSetting("selectedModel", "domain-model");
        host.SetSetting("fetchedCustomModels", new[]
        {
            new Reson8CustomModel("domain-model", "Domain Model", null, null)
        });

        using var httpClient = new HttpClient(handler);
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(
            BuildPcm16Wav([0x01, 0x00, 0xFF, 0xFF]),
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(0.000125, result.DurationSeconds, precision: 6);
    }

    [Fact]
    public async Task TranscribeAsync_UsesCustomBaseUrlAndAuthHeaderAndOmitsAutoLanguage()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://proxy.example.test/v1/speech-to-text/prerecorded", request.RequestUri?.GetLeftPart(UriPartial.Path));
            Assert.DoesNotContain("language=", request.RequestUri?.Query);
            Assert.DoesNotContain("custom_model_id=", request.RequestUri?.Query);
            Assert.True(request.Headers.TryGetValues("X-Api-Key", out var values));
            Assert.Equal("reson-key", Assert.Single(values));
            Assert.False(request.Headers.Contains("Authorization"));

            return JsonResponse("""{ "text": "Hello" }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        host.SetSetting("customBaseURL", "https://proxy.example.test/");
        host.SetSetting("customAuthHeader", "X-Api-Key");

        using var httpClient = new HttpClient(handler);
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), "auto", false, null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_ThrowsActionableMessagesForKnownHttpErrors()
    {
        var statuses = new Queue<HttpStatusCode>([
            HttpStatusCode.Unauthorized,
            HttpStatusCode.NotFound,
            HttpStatusCode.RequestEntityTooLarge,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError
        ]);
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "code": "ERR", "message": "details" }""", statuses.Dequeue()));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        using var httpClient = new HttpClient(handler);
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var wav = BuildPcm16Wav([0x00, 0x00]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.TranscribeAsync(wav, null, false, null, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.TranscribeAsync(wav, null, false, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.TranscribeAsync(wav, null, false, null, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.TranscribeAsync(wav, null, false, null, CancellationToken.None));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sut.TranscribeAsync(wav, null, false, null, CancellationToken.None));
        Assert.Contains("Reson8 server error", ex.Message);
    }

    [Fact]
    public async Task FetchCustomModelsAsync_LoadsAndPersistsModels()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.reson8.dev/v1/custom-model", request.RequestUri?.ToString());
            Assert.Equal("ApiKey reson-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                [
                  { "id": "domain-model", "name": "Domain Model", "description": "Support vocabulary", "phraseCount": 42 }
                ]
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        using var httpClient = new HttpClient(handler);
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchCustomModelsAsync();
        sut.SetFetchedCustomModels(models);

        Assert.Equal("domain-model", Assert.Single(models).Id);
        Assert.Equal(["__default__", "domain-model"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        Assert.Equal("domain-model", Assert.Single(host.GetSetting<List<Reson8CustomModel>>("fetchedCustomModels")!).Id);
    }

    [Fact]
    public void StreamingSession_BuildsExpectedUrisHeadersAndCollectsTranscriptEvents()
    {
        var uri = Reson8StreamingSession.BuildRealtimeUri("https://api.reson8.dev", "domain-model", "de");
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("api.reson8.dev", uri.Host);
        Assert.Equal("/v1/speech-to-text/realtime", uri.AbsolutePath);
        Assert.Contains("encoding=pcm_s16le", uri.Query);
        Assert.Contains("sample_rate=16000", uri.Query);
        Assert.Contains("channels=1", uri.Query);
        Assert.Contains("include_interim=true", uri.Query);
        Assert.Contains("language=de", uri.Query);
        Assert.Contains("custom_model_id=domain-model", uri.Query);

        var localUri = Reson8StreamingSession.BuildRealtimeUri("http://localhost:8080/base", "__default__", "auto");
        Assert.Equal("ws", localUri.Scheme);
        Assert.Equal(8080, localUri.Port);
        Assert.DoesNotContain("language=", localUri.Query);
        Assert.DoesNotContain("custom_model_id=", localUri.Query);

        var headers = Reson8StreamingSession.CreateStreamingHeaders("reson-key", "Authorization");
        Assert.Equal("ApiKey reson-key", headers["Authorization"]);
        var customHeaders = Reson8StreamingSession.CreateStreamingHeaders("reson-key", "X-Api-Key");
        Assert.Equal("reson-key", customHeaders["X-Api-Key"]);

        var collector = new Reson8TranscriptCollector();
        var partial = collector.ApplyEvent("""{ "type": "transcript", "text": "Hel", "is_final": false }""");
        Assert.Equal(new StreamingTranscriptEvent("Hel", false), partial);

        var final = collector.ApplyEvent("""{ "type": "transcript", "text": "Hello", "is_final": true }""");
        Assert.Equal(new StreamingTranscriptEvent("Hello", true), final);
        Assert.Equal("Hello", collector.FinalText);

        Assert.Null(collector.ApplyEvent("""{ "type": "flush_confirmation" }"""));
        Assert.True(collector.IsFlushConfirmed);
    }

    [Fact]
    public void StreamingCollector_ThrowsActionableMessageForApiErrors()
    {
        var collector = new Reson8TranscriptCollector();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            collector.ApplyEvent("""{ "type": "error", "message": "Invalid API key" }"""));

        Assert.Contains("Invalid API key", ex.Message);
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Reson8", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static JsonElement LoadLocalization(string language)
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeLocalizationPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Reson8", "Localization", $"{language}.json");
        var localizationPath = Path.GetFullPath(relativeLocalizationPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(localizationPath));
        return doc.RootElement.Clone();
    }

    private static byte[] BuildPcm16Wav(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(16000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
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
