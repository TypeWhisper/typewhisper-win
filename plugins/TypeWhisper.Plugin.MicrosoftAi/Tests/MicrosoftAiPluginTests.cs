using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.MicrosoftAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class MicrosoftAiPluginTests
{
    [Fact]
    public void ManifestAndPlugin_ExposeMicrosoftAiMetadataAndFallbackModels()
    {
        var manifestPath = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.MicrosoftAi", "manifest.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var sut = new MicrosoftAiPlugin();

        Assert.Equal("com.typewhisper.microsoft-ai", sut.PluginId);
        Assert.Equal("microsoft-ai", sut.ProviderId);
        Assert.Equal("Microsoft AI (MAI Transcribe)", sut.ProviderDisplayName);
        Assert.Equal(sut.PluginId, manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal(sut.PluginVersion, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("transcription", manifest.RootElement.GetProperty("category").GetString());
        Assert.True(manifest.RootElement.GetProperty("requiresApiKey").GetBoolean());
        Assert.Equal(
            [MicrosoftAiPlugin.DefaultModelId, MicrosoftAiPlugin.LegacyModelId],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal([60, 43], sut.TranscriptionModels.Select(model => model.LanguageCount).ToArray());
        Assert.False(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.True(sut.SupportsDictionaryTerms);
        Assert.Equal(new DictionaryTermsBudget(MaxTerms: 500, MaxTotalChars: 20_000), sut.DictionaryTermsBudget);
        Assert.Equal(60, sut.SupportedLanguages.Count);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("yue", sut.SupportedLanguages);
    }

    [Theory]
    [InlineData("typewhisper-speech", "https://typewhisper-speech.cognitiveservices.azure.com/")]
    [InlineData("https://eastus.api.cognitive.microsoft.com/", "https://eastus.api.cognitive.microsoft.com/")]
    [InlineData("https://example.services.ai.azure.com", "https://example.services.ai.azure.com/")]
    [InlineData("https://example.openai.azure.us", "https://example.openai.azure.us/")]
    public void NormalizeEndpoint_AcceptsResourceNamesAndAzureHosts(string input, string expected)
    {
        Assert.Equal(expected, MicrosoftAiPlugin.NormalizeEndpoint(input)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a resource")]
    [InlineData("http://example.cognitiveservices.azure.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.cognitiveservices.azure.com/path")]
    [InlineData("https://user@example.cognitiveservices.azure.com")]
    [InlineData("https://example.cognitiveservices.azure.com?key=secret")]
    public void NormalizeEndpoint_RejectsUnsafeOrMalformedValues(string input)
    {
        Assert.Null(MicrosoftAiPlugin.NormalizeEndpoint(input));
    }

    [Fact]
    public void RegionalEndpointAvailability_IdentifiesUnsupportedRegions()
    {
        var westEurope = MicrosoftAiPlugin.NormalizeEndpoint(
            "https://westeurope.api.cognitive.microsoft.com")!;
        var northEurope = MicrosoftAiPlugin.NormalizeEndpoint(
            "https://northeurope.api.cognitive.microsoft.com")!;
        var resource = MicrosoftAiPlugin.NormalizeEndpoint("speech-demo")!;

        Assert.Equal("westeurope", MicrosoftAiPlugin.GetRegionalEndpointRegion(westEurope));
        Assert.Equal("westeurope", MicrosoftAiPlugin.GetUnsupportedMaiRegion(westEurope));
        Assert.Null(MicrosoftAiPlugin.GetUnsupportedMaiRegion(northEurope));
        Assert.Null(MicrosoftAiPlugin.GetRegionalEndpointRegion(resource));
    }

    [Fact]
    public async Task ConnectionAndSettings_ArePersistedAndReloaded()
    {
        var host = new TestPluginHostServices();
        using var sut = new MicrosoftAiPlugin();
        await sut.ActivateAsync(host);

        sut.SetEndpoint("speech-demo");
        await sut.SetApiKeyAsync(" azure-key ");
        sut.SetTranscriptStyle(MicrosoftAiTranscriptStyle.Verbatim);
        sut.SetSpeakerDiarizationEnabled(true);
        sut.SelectModel(MicrosoftAiPlugin.LegacyModelId);

        Assert.True(sut.IsConfigured);
        Assert.Equal("azure-key", host.Secrets["api-key"]);
        Assert.Equal(
            "https://speech-demo.cognitiveservices.azure.com",
            host.GetSetting<string>("endpoint"));
        Assert.Equal("verbatim", host.GetSetting<string>("transcriptStyle"));
        Assert.True(host.GetSetting<bool>("speakerDiarizationEnabled"));
        Assert.Equal(MicrosoftAiPlugin.LegacyModelId, host.GetSetting<string>("selectedModel"));

        using var reloaded = new MicrosoftAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.True(reloaded.IsConfigured);
        Assert.Equal(MicrosoftAiPlugin.LegacyModelId, reloaded.SelectedModelId);
        Assert.Equal(MicrosoftAiTranscriptStyle.Verbatim, reloaded.TranscriptStyle);
        Assert.False(reloaded.SelectedModelSupportsDiarization);

        reloaded.SetEndpoint("");
        await reloaded.SetApiKeyAsync("");
        Assert.False(reloaded.IsConfigured);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    [Fact]
    public async Task TranscribeAsync_BuildsMultipartRequestAndParsesSpeakersAndTimings()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://speech-demo.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
                request.RequestUri?.AbsoluteUri);
            Assert.True(request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var keys));
            Assert.Equal("azure-key", Assert.Single(keys));
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Equal("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);

            Assert.NotNull(body);
            Assert.Contains("name=definition", body);
            Assert.Contains("name=audio", body);
            Assert.Contains("filename=audio.wav", body);
            Assert.Contains("\"model\":\"MAI-Transcribe-2\"", body);
            Assert.Contains("\"locales\":[\"de-DE\"]", body);
            Assert.Contains("\"timestamps\":\"segment\"", body);
            Assert.Contains("\"transcribeStyle\":\"verbatim\"", body);
            Assert.Contains("\"diarization\":{\"enabled\":true}", body);
            Assert.Contains("\"phraseList\":{\"phrases\":[\"TypeWhisper\",\"Azure\"]}", body);
            Assert.Contains("\"profanityFilterMode\":\"None\"", body);
            return JsonResponse(SuccessResponse);
        });
        var host = ConfiguredHost();
        host.SetSetting("transcriptStyle", "verbatim");
        host.SetSetting("speakerDiarizationEnabled", true);
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de_DE",
            translate: false,
            "TypeWhisper, Azure",
            CancellationToken.None);

        Assert.Equal("Speaker 1: Hallo Welt\nSpeaker 2: Willkommen", result.Text);
        Assert.Equal("de-DE", result.DetectedLanguage);
        Assert.Equal(2.75, result.DurationSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("Speaker 1: Hallo Welt", result.Segments[0].Text);
        Assert.Equal(0.25, result.Segments[0].Start);
        Assert.Equal(1.75, result.Segments[0].End);
        Assert.Equal("Speaker 2: Willkommen", result.Segments[1].Text);
    }

    [Fact]
    public void BuildDefinition_UsesLegacyStyleContractAndOmitsUnsupportedDiarization()
    {
        using var clean = JsonDocument.Parse(MicrosoftAiPlugin.BuildDefinitionJson(
            MicrosoftAiPlugin.LegacyModelId,
            null,
            [],
            MicrosoftAiTranscriptStyle.Clean,
            speakerDiarizationEnabled: true));
        var cleanRoot = clean.RootElement;
        var cleanEnhanced = cleanRoot.GetProperty("enhancedMode");
        Assert.False(cleanEnhanced.TryGetProperty("modelOptions", out _));
        Assert.False(cleanEnhanced.TryGetProperty("transcribeStyle", out _));
        Assert.False(cleanRoot.TryGetProperty("diarization", out _));
        Assert.False(cleanRoot.TryGetProperty("locales", out _));

        using var verbatim = JsonDocument.Parse(MicrosoftAiPlugin.BuildDefinitionJson(
            MicrosoftAiPlugin.LegacyModelId,
            "en",
            ["TypeWhisper"],
            MicrosoftAiTranscriptStyle.Verbatim,
            speakerDiarizationEnabled: false));
        Assert.Equal(
            "verbatim",
            verbatim.RootElement.GetProperty("enhancedMode").GetProperty("transcribeStyle").GetString());
        Assert.Equal("en", verbatim.RootElement.GetProperty("locales")[0].GetString());
    }

    [Fact]
    public async Task MultipleLanguageHints_UseAutomaticDetection()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            Assert.DoesNotContain("\"locales\"", body);
            return JsonResponse("""{"combinedPhrases":[{"text":"Hello"}],"phrases":[]}""");
        });
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(ConfiguredHost());

        await sut.TranscribeWithLanguageHintsAsync(
            [1],
            ["de", "en"],
            false,
            null,
            CancellationToken.None);
    }

    [Fact]
    public async Task RefreshModelCatalog_FiltersPersistsAndMergesMaiModels()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(
                "https://speech-demo.cognitiveservices.azure.com/openai/v1/models",
                request.RequestUri?.AbsoluteUri);
            Assert.True(request.Headers.TryGetValues("api-key", out var keys));
            Assert.Equal("azure-key", Assert.Single(keys));
            return JsonResponse("""
                {"data":[
                  {"id":"gpt-5"},
                  {"id":"mai-transcribe-2-preview"},
                  {"id":"mai-transcribe-2"}
                ]}
                """);
        });
        var host = ConfiguredHost();
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(host);

        Assert.True(await sut.RefreshModelCatalogAsync());
        Assert.Equal(
            ["MAI-Transcribe-2", "mai-transcribe-2-preview", "MAI-Transcribe-1.5"],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal(
            ["mai-transcribe-2", "mai-transcribe-2-preview"],
            host.GetSetting<IReadOnlyList<string>>("cachedModels"));
    }

    [Fact]
    public async Task UnavailableModelCatalog_KeepsStaticFallbacks()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"Not found"}}"""),
        });
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(ConfiguredHost());

        Assert.False(await sut.RefreshModelCatalogAsync());
        Assert.Equal(
            [MicrosoftAiPlugin.DefaultModelId, MicrosoftAiPlugin.LegacyModelId],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PluginRequestFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, PluginRequestFailureKind.Authentication)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, PluginRequestFailureKind.RateLimit)]
    [InlineData(HttpStatusCode.InternalServerError, PluginRequestFailureKind.ServerError)]
    public async Task HttpFailures_AreMappedWithoutExposingCredentials(
        HttpStatusCode statusCode,
        PluginRequestFailureKind expectedKind)
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""{"message":"request failed for azure-key"}"""),
        });
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(ConfiguredHost());

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(
            [1], null, false, null, CancellationToken.None));
        Assert.Equal(expectedKind, error.FailureKind);
        Assert.Equal((int)statusCode, error.HttpStatusCode);
        Assert.DoesNotContain("azure-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedRegionalEndpoint_ReturnsActionableGuidance()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"code\":\"InvalidRequest\",\"message\":\"Enhanced mode with model is currently not supported yet.\"}"),
        });
        var host = ConfiguredHost("https://westeurope.api.cognitive.microsoft.com");
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(
            [1], "de", false, null, CancellationToken.None));
        Assert.Contains("westeurope", error.Message);
        Assert.Contains("northeurope", error.Message);
        Assert.DoesNotContain("azure-key", error.Message);
    }

    [Fact]
    public async Task ConfigurationTranslationAndDurationLimits_AreRejectedBeforeNetworking()
    {
        using var unconfigured = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        await unconfigured.ActivateAsync(new TestPluginHostServices());
        var missing = await Assert.ThrowsAsync<PluginRequestException>(() => unconfigured.TranscribeAsync(
            [1], null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, missing.FailureKind);

        using var configured = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        await configured.ActivateAsync(ConfiguredHost());
        var translation = await Assert.ThrowsAsync<PluginRequestException>(() => configured.TranscribeAsync(
            [1], null, true, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.InvalidRequest, translation.FailureKind);

        var duration = await Assert.ThrowsAsync<PluginRequestException>(() => configured.TranscribeAsync(
            LongDurationWav(), null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, duration.FailureKind);
    }

    [Fact]
    public void SettingsFiles_ExposeMaskedKeyAndAllOptionsInEnglishAndGerman()
    {
        var xaml = TestFile.ReadProjectFile(
            "plugins", "TypeWhisper.Plugin.MicrosoftAi", "MicrosoftAiSettingsView.xaml");
        Assert.Contains("<PasswordBox", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"MicrosoftAiEndpoint\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"MicrosoftAiModel\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"MicrosoftAiTranscriptStyle\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"MicrosoftAiSpeakerDiarization\"", xaml);

        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(TestFile.ReadProjectFile(
            "plugins", "TypeWhisper.Plugin.MicrosoftAi", "Localization", "en.json"));
        var german = JsonSerializer.Deserialize<Dictionary<string, string>>(TestFile.ReadProjectFile(
            "plugins", "TypeWhisper.Plugin.MicrosoftAi", "Localization", "de.json"));
        Assert.NotNull(english);
        Assert.NotNull(german);
        Assert.Equal(english.Keys.OrderBy(key => key), german.Keys.OrderBy(key => key));
    }

    private const string SuccessResponse = """
        {
          "durationMilliseconds":2750,
          "combinedPhrases":[{"channel":0,"text":"Hallo Welt Willkommen"}],
          "phrases":[
            {"offsetMilliseconds":250,"durationMilliseconds":1500,"text":"Hallo Welt","locale":"de-DE","speaker":1},
            {"offsetMilliseconds":1750,"durationMilliseconds":1000,"text":"Willkommen","locale":"de-DE","speaker":"2"}
          ]
        }
        """;

    private static TestPluginHostServices ConfiguredHost(
        string endpoint = "https://speech-demo.cognitiveservices.azure.com")
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "azure-key";
        host.SetSetting("endpoint", endpoint);
        return host;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static byte[] LongDurationWav()
    {
        const int byteRate = 10;
        const int dataSize = byteRate * 7200;
        var bytes = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(5).CopyTo(bytes, 24);
        BitConverter.GetBytes(byteRate).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("HTTP should not have been called.");
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];

        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public List<string> LogMessages { get; } = [];

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
                ? value.Deserialize<T>()
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) => LogMessages.Add(message);
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
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
