using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiPluginTests
{
    [Fact]
    public void PluginVersionAndManifest_TargetTypeWhisper14AndAdvertiseTts()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.OpenAi", "manifest.json"));
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new OpenAiPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
        Assert.Equal("1.4.0", manifest.MinHostVersion);
        Assert.Contains("text-to-speech", manifest.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["transcription", "llm", "tts"], manifest.Categories);
    }

    [Fact]
    public void SettingsViewXaml_KeepsTranscriptionModelOutOfPluginSettings()
    {
        var viewPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.OpenAi", "OpenAiSettingsView.xaml"));
        var xaml = File.ReadAllText(viewPath);

        Assert.DoesNotContain("TranscriptionModelComboBox", xaml);
        Assert.DoesNotContain("OnTranscriptionModelSelectionChanged", xaml);
        Assert.Contains("TemperatureModeComboBox", xaml);
        Assert.Contains("TemperatureSlider", xaml);
        Assert.Contains("OnTemperatureModeSelectionChanged", xaml);
        Assert.Contains("OnTemperatureValueChanged", xaml);
    }

    [Fact]
    public async Task ActivateAsync_DefaultsToGPT55AndRealtimeCapableOpenAiModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.IsAssignableFrom<ITtsProviderPlugin>(sut);
        Assert.Equal("gpt-5.5", sut.SupportedModels.First().Id);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == OpenAiRealtimeStreamingSession.ModelId);

        sut.SelectModel(OpenAiRealtimeStreamingSession.ModelId);

        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task LocalSelectionChanges_PersistWithoutRebuildingCapabilities()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        sut.SelectVoice("nova");
        sut.SelectLlmModel("gpt-4o");

        Assert.Equal("nova", host.GetSetting<string>("selectedVoice"));
        Assert.Equal("gpt-4o", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public void UsesResponsesApi_OnlyForGPT5Models()
    {
        Assert.True(OpenAiPlugin.UsesResponsesApi("gpt-5.5"));
        Assert.True(OpenAiPlugin.UsesResponsesApi("gpt-5.4-mini"));
        Assert.False(OpenAiPlugin.UsesResponsesApi("gpt-4o"));
    }

    [Fact]
    public void ResponsesRequestBody_UsesStoreFalseAndReasoning()
    {
        var body = OpenAiResponsesClient.CreateRequestBody(
            model: "gpt-5.5",
            systemPrompt: "Fix grammar",
            userText: "hello world",
            reasoningEffort: "medium");

        Assert.Equal("gpt-5.5", body["model"].GetString());
        Assert.False(body["store"].GetBoolean());
        Assert.Equal("Fix grammar", body["instructions"].GetString());
        Assert.Equal("medium", body["reasoning"].GetProperty("effort").GetString());
        Assert.Equal("user", body["input"][0].GetProperty("role").GetString());
    }

    [Fact]
    public void ResponsesParser_ExtractsOutputTextFromOutputArray()
    {
        var json = """
        {
          "id": "resp_123",
          "output": [
            {
              "type": "message",
              "content": [
                { "type": "output_text", "text": "Cleaned transcript" }
              ]
            }
          ]
        }
        """;

        Assert.Equal("Cleaned transcript", OpenAiResponsesClient.ParseResponse(json));
    }

    [Fact]
    public void RealtimeUri_UsesGAEndpointWithoutBetaHeader()
    {
        var headers = OpenAiRealtimeStreamingSession.CreateRealtimeHeaders("sk-test");
        var uri = OpenAiRealtimeStreamingSession.BuildRealtimeUri();

        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", uri.AbsoluteUri);
        Assert.Equal("Bearer sk-test", headers["Authorization"]);
        Assert.DoesNotContain(
            headers.Keys,
            header => header.Equals("OpenAI-Beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RealtimeSessionUpdatePayload_UsesTranscriptionSessionAndOmitsPrompt()
    {
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload("de", "TypeWhisper, OpenAI");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var session = root.GetProperty("session");
        var input = session.GetProperty("audio").GetProperty("input");
        var transcription = input.GetProperty("transcription");

        Assert.Equal("session.update", root.GetProperty("type").GetString());
        Assert.Equal("transcription", session.GetProperty("type").GetString());
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("gpt-realtime-whisper", transcription.GetProperty("model").GetString());
        Assert.Equal("de", transcription.GetProperty("language").GetString());
        Assert.False(transcription.TryGetProperty("prompt", out _));
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
    }

    [Fact]
    public void RealtimeAudioPayload_Resamples16kPcmTo24kPcm()
    {
        var oneSecond16kPcm = new byte[16_000 * sizeof(short)];

        var payload = OpenAiRealtimeStreamingSession.CreateAudioAppendPayload(oneSecond16kPcm);

        using var doc = JsonDocument.Parse(payload);
        var bytes = Convert.FromBase64String(doc.RootElement.GetProperty("audio").GetString()!);
        Assert.Equal("input_audio_buffer.append", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(24_000 * sizeof(short), bytes.Length);
    }

    [Fact]
    public void RealtimeTranscriptCollector_PublishesDeltaAndCompletedText()
    {
        var collector = new OpenAiRealtimeTranscriptCollector();

        var delta = collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_1","delta":"Hello"}""",
            out var deltaEvent);
        var completed = collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":"Hello world"}""",
            out var completedEvent);

        Assert.True(delta);
        Assert.Equal(new StreamingTranscriptEvent("Hello", false), deltaEvent);
        Assert.True(completed);
        Assert.Equal(new StreamingTranscriptEvent("Hello world", true), completedEvent);
        Assert.Equal("Hello world", collector.CurrentText);
    }

    [Fact]
    public void TtsConfiguration_UsesMiniTtsPcmAndDefaultVoice()
    {
        Assert.Equal("marin", OpenAiTtsConfiguration.DefaultVoiceId);
        Assert.Equal(13, OpenAiTtsConfiguration.AvailableVoices.Count);
        Assert.Contains(OpenAiTtsConfiguration.AvailableVoices, voice => voice.Id == "cedar");

        var body = OpenAiTtsConfiguration.CreateRequestBody(
            text: "Hallo Welt",
            voice: null,
            instructions: "Speak calmly.");

        Assert.Equal("gpt-4o-mini-tts", body["model"].GetString());
        Assert.Equal("marin", body["voice"].GetString());
        Assert.Equal("Hallo Welt", body["input"].GetString());
        Assert.Equal("Speak calmly.", body["instructions"].GetString());
        Assert.Equal("pcm", body["response_format"].GetString());
    }

    [Fact]
    public async Task ProcessAsync_UsesResponsesApiForGPT5Models()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler(async (request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            await Task.Yield();
            return JsonResponse("""{"output_text":"Cleaned transcript"}""");
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("Fix grammar", "hello world", "gpt-5.5", CancellationToken.None);

        Assert.Equal("Cleaned transcript", result);
        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("https://api.openai.com/v1/responses", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("sk-live", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.NotNull(capturedBody);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("medium", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_QueriesModelsEndpointFiltersChatModelsAndPersists()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""
            {
              "data": [
                { "id": "whisper-1", "owned_by": "openai" },
                { "id": "gpt-4o-mini-transcribe", "owned_by": "openai" },
                { "id": "gpt-4o-mini-transcribe-2025-03-20", "owned_by": "openai" },
                { "id": "gpt-4o-transcribe-diarize", "owned_by": "openai" },
                { "id": "gpt-4o-realtime-preview-2024-12-17", "owned_by": "openai" },
                { "id": "gpt-4o-search-preview", "owned_by": "openai" },
                { "id": "gpt-audio-2025-08-28", "owned_by": "openai" },
                { "id": "gpt-image-1", "owned_by": "openai" },
                { "id": "o4-mini", "owned_by": "openai" },
                { "id": "gpt-4.1-mini", "owned_by": "openai" },
                { "id": "tts-1", "owned_by": "openai" }
              ]
            }
            """));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";
        host.SetSetting("selectedLLMModel", "stale-model");
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-4.1-mini", "o4-mini"], models.Select(m => m.Id).ToArray());
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("gpt-4.1-mini", sut.SelectedLlmModelId);
        Assert.Equal("gpt-4.1-mini", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal("https://api.openai.com/v1/models", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("sk-live", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);

        var cachedModels = host.GetSetting<List<OpenAiFetchedModel>>("fetchedLLMModels");
        Assert.NotNull(cachedModels);
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], cachedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_UsesChatGptCatalogWithoutModelsEndpoint()
    {
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonResponse("{}"));
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("selectedLLMModel", "stale-model");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal("gpt-5.5", models.First().Id);
        Assert.Contains(models, model => model.Id == "gpt-5.3-codex-spark");
        Assert.Equal("gpt-5.5", sut.SelectedLlmModelId);
        Assert.Equal("gpt-5.5", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal(0, requests);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ProcessAsync_UsesReasoningChatCompletionParametersForOModels()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return Task.FromResult(JsonResponse("""
            {
              "choices": [
                { "message": { "content": "Reasoned result" } }
              ]
            }
            """));
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("Fix grammar", "hello world", "o4-mini", CancellationToken.None);

        Assert.Equal("Reasoned result", result);
        Assert.Equal("https://api.openai.com/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        Assert.NotNull(capturedBody);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("o4-mini", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task ProcessAsync_UsesCustomTemperatureForApiKeyChatCompletions()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return Task.FromResult(JsonResponse("""
            {
              "choices": [
                { "message": { "content": "Warmer result" } }
              ]
            }
            """));
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";
        host.SetSetting("llmTemperatureMode", "custom");
        host.SetSetting("llmTemperatureValue", 1.2);

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("Fix grammar", "hello world", "gpt-4o", CancellationToken.None);

        Assert.Equal("Warmer result", result);
        Assert.NotNull(capturedBody);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4o", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(1.2, doc.RootElement.GetProperty("temperature").GetDouble(), precision: 3);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task ChatGptAuthMode_IsAvailableWithBrowserLoginTokensWithoutApiKey()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("oauthAccountID", "acct_123");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));

        var sut = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}")))));
        await sut.ActivateAsync(host);

        Assert.Equal(OpenAiAuthMode.ChatGpt, sut.AuthMode);
        Assert.False(sut.IsConfigured);
        Assert.True(sut.IsAvailable);
        Assert.Equal("gpt-5.5", sut.SupportedModels.First().Id);
    }

    [Fact]
    public void ChatGptAuthorizeUri_UsesPkceLoopbackAndOpenAiIssuer()
    {
        var uri = OpenAiOAuthClient.BuildAuthorizeUri(
            state: "state_123",
            pkce: new OpenAiPkceCodes("verifier", "challenge"));
        var query = ParseQuery(uri);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("auth.openai.com", uri.Host);
        Assert.Equal("/oauth/authorize", uri.AbsolutePath);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(OpenAiOAuthClient.ClientId, query["client_id"]);
        Assert.Equal(OpenAiOAuthClient.RedirectUri, query["redirect_uri"]);
        Assert.Equal("challenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("state_123", query["state"]);
    }

    [Fact]
    public void LoopbackOAuthServer_ParsesCallbackRequestLineAndRejectsWrongState()
    {
        var code = OpenAiLoopbackOAuthServer.ParseAuthorizationCode(
            "GET /auth/callback?code=abc123&state=expected HTTP/1.1",
            "expected");

        Assert.Equal("abc123", code);
        Assert.Throws<InvalidOperationException>(() =>
            OpenAiLoopbackOAuthServer.ParseAuthorizationCode(
                "GET /auth/callback?code=abc123&state=wrong HTTP/1.1",
                "expected"));
    }

    [Fact]
    public async Task ProcessAsync_UsesChatGptEndpointWhenChatGptAuthModeIsSelected()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return Task.FromResult(JsonResponse("""{"output_text":"Cleaned with ChatGPT"}"""));
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthAccountID", "acct_123");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("Fix grammar", "hello world", "gpt-5.5", CancellationToken.None);

        Assert.Equal("Cleaned with ChatGPT", result);
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal("acct_123", capturedRequest?.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("text/event-stream", capturedRequest?.Headers.Accept.Single().MediaType);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-5.5", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void ChatGptResponseParser_ExtractsServerSentEventText()
    {
        var stream = """
        event: response.output_text.delta
        data: {"type":"response.output_text.delta","delta":"Hello"}
        event: response.output_text.delta
        data: {"type":"response.output_text.delta","delta":" world"}
        data: [DONE]

        """;

        Assert.Equal("Hello world", OpenAiChatGptClient.ParseResponseText(stream));
    }

    [Fact]
    public async Task ImportExistingLogin_LoadsTokensFromCodexAuthFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var authPath = Path.Combine(tempDir, "auth.json");
        await File.WriteAllTextAsync(authPath, """
        {
          "tokens": {
            "access_token": "access-token",
            "refresh_token": "refresh-token",
            "id_token": null,
            "account_id": "acct_from_file"
          }
        }
        """);
        var host = new TestPluginHostServices();
        var sut = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}")))));
        await sut.ActivateAsync(host);

        try
        {
            await sut.ImportExistingLoginAsync(authPath);

            Assert.Equal("access-token", host.Secrets["oauth-access-token"]);
            Assert.Equal("refresh-token", host.Secrets["oauth-refresh-token"]);
            Assert.Equal("acct_from_file", host.GetSetting<string>("oauthAccountID"));
            Assert.True(sut.HasChatGptCredentials);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : "");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responder(request, body);
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
