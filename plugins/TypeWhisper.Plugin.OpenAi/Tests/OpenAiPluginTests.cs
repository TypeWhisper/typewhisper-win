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
    public void PluginVersionAndManifest_TargetTypeWhisper10AndAdvertiseTts()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.OpenAi", "manifest.json"));
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            manifestJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        using var manifestDocument = JsonDocument.Parse(manifestJson);

        var sut = new OpenAiPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
        Assert.Equal("1.0.0", manifest.MinHostVersion);
        Assert.Contains("text-to-speech", manifest.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API key", manifest.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["transcription", "llm", "tts"], manifest.Categories);
        Assert.True(manifestDocument.RootElement.GetProperty("requiresApiKey").GetBoolean());
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
    public async Task ActivateAsync_DefaultsToGPT55AndGPTTranscribe()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.IsAssignableFrom<ITtsProviderPlugin>(sut);
        Assert.Equal("gpt-5.5", sut.SupportedModels.First().Id);
        Assert.Equal("gpt-transcribe", sut.SelectedModelId);
        Assert.Equal("gpt-transcribe", sut.TranscriptionModels.First().Id);
        Assert.True(sut.SupportsDictionaryTerms);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == OpenAiRealtimeStreamingSession.LiveModelId);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == OpenAiRealtimeStreamingSession.LegacyModelId);

        sut.SelectModel(OpenAiRealtimeStreamingSession.LiveModelId);

        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsDictionaryTerms);
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task ActivateAsync_PreservesPersistedLegacyTranscriptionModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        host.SetSetting("selectedModel", "whisper-1");

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("whisper-1", sut.SelectedModelId);
        Assert.True(sut.SupportsTranslation);
    }

    [Fact]
    public async Task ActivateAsync_InvalidTranscriptionModelFallsBackToGPTTranscribe()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        host.SetSetting("selectedModel", "retired-model");

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("gpt-transcribe", sut.SelectedModelId);
    }

    [Fact]
    public async Task ActivateAsync_UsesCachedAvailableTranscriptionModelsAndPreservesSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        host.SetSetting(
            "fetchedTranscriptionModels",
            new List<OpenAiFetchedModel>
            {
                new("gpt-live-transcribe-2026-07-28", "openai"),
                new("whisper-1", "openai"),
            });
        host.SetSetting("selectedModel", "gpt-live-transcribe-2026-07-28");

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(
            ["whisper-1", "gpt-live-transcribe-2026-07-28"],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal("gpt-live-transcribe-2026-07-28", sut.SelectedModelId);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsDictionaryTerms);
    }

    [Fact]
    public async Task ActivateAsync_UsesCachedAccountSpecificChatGptModels()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting(
            "fetchedChatGPTModels",
            new List<OpenAiChatGptModel>
            {
                new("gpt-5.6-sol", "GPT-5.6-Sol", "list", 1, ["plus"]),
                new("gpt-5.5", "GPT-5.5", "list", 7, ["plus"]),
            });
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(
            ["gpt-5.6-sol", "gpt-5.5"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Equal("gpt-5.6-sol", sut.SelectedLlmModelId);
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
    public void RealtimeLegacySessionUpdatePayload_PreservesSingularLanguageShape()
    {
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
            OpenAiRealtimeStreamingSession.LegacyModelId,
            ["de", "en"],
            "TypeWhisper, OpenAI");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var session = root.GetProperty("session");
        var input = session.GetProperty("audio").GetProperty("input");
        var transcription = input.GetProperty("transcription");

        Assert.Equal("session.update", root.GetProperty("type").GetString());
        Assert.Equal("transcription", session.GetProperty("type").GetString());
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal(OpenAiRealtimeStreamingSession.LegacyModelId, transcription.GetProperty("model").GetString());
        Assert.Equal("de", transcription.GetProperty("language").GetString());
        Assert.False(transcription.TryGetProperty("languages", out _));
        Assert.False(transcription.TryGetProperty("prompt", out _));
        Assert.False(transcription.TryGetProperty("delay", out _));
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
    }

    [Fact]
    public void RealtimeLiveSessionUpdatePayload_UsesLanguagesAndLowDelay()
    {
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
            OpenAiRealtimeStreamingSession.LiveModelId,
            ["de", "en"],
            prompt: null);

        using var doc = JsonDocument.Parse(json);
        var transcription = doc.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("transcription");

        Assert.Equal(OpenAiRealtimeStreamingSession.LiveModelId, transcription.GetProperty("model").GetString());
        Assert.Equal(["de", "en"], transcription.GetProperty("languages")
            .EnumerateArray()
            .Select(language => language.GetString()!)
            .ToArray());
        Assert.Equal(OpenAiRealtimeStreamingSession.LiveDelay, transcription.GetProperty("delay").GetString());
        Assert.False(transcription.TryGetProperty("language", out _));
        Assert.False(transcription.TryGetProperty("prompt", out _));
    }

    [Fact]
    public void RealtimeLiveSnapshotSessionUpdatePayload_UsesLiveModelShape()
    {
        const string snapshotModel = "gpt-live-transcribe-2026-07-28";
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
            snapshotModel,
            ["de", "en"],
            prompt: null);

        using var doc = JsonDocument.Parse(json);
        var transcription = doc.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("transcription");

        Assert.Equal(snapshotModel, transcription.GetProperty("model").GetString());
        Assert.Equal(["de", "en"], transcription.GetProperty("languages")
            .EnumerateArray()
            .Select(language => language.GetString()!)
            .ToArray());
        Assert.Equal(OpenAiRealtimeStreamingSession.LiveDelay, transcription.GetProperty("delay").GetString());
        Assert.False(transcription.TryGetProperty("language", out _));
    }

    [Fact]
    public async Task GPTTranscribeRequest_UsesOrderedPluralLanguagesAndDictionaryPrompt()
    {
        var fields = new List<(string Name, string Value)>();
        var handler = new CapturingHandler(async (request, _) =>
        {
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.Equal("Bearer sk-test", request.Headers.Authorization?.ToString());

            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                fields.Add((name, await part.ReadAsStringAsync()));
            }

            return JsonResponse("""
            {
              "text": "  Hallo world  ",
              "languages": [{ "code": "de" }],
              "duration": 2.5
            }
            """);
        });
        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeWithLanguageHintsAsync(
            [0, 1, 2, 3],
            [" de ", "auto", "en", "DE"],
            translate: false,
            "TypeWhisper, OpenAI",
            CancellationToken.None);

        Assert.Equal("Hallo world", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds);
        Assert.Equal("gpt-transcribe", fields.Single(field => field.Name == "model").Value);
        Assert.DoesNotContain(fields, field => field.Name == "response_format");
        Assert.Equal(
            ["de", "en"],
            fields.Where(field => field.Name == "languages[]").Select(field => field.Value).ToArray());
        Assert.DoesNotContain(fields, field => field.Name == "language");
        Assert.Equal("TypeWhisper, OpenAI", fields.Single(field => field.Name == "prompt").Value);
    }

    [Fact]
    public async Task DiscoveredGPTTranscribeSnapshot_UsesPluralRequestShape()
    {
        const string snapshotModel = "gpt-transcribe-2026-07-28";
        var fields = new List<(string Name, string Value)>();
        var handler = new CapturingHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse($$"""
                {
                  "data": [
                    { "id": "{{snapshotModel}}", "owned_by": "openai" }
                  ]
                }
                """);
            }

            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                fields.Add((name, await part.ReadAsStringAsync()));
            }

            return JsonResponse("""{"text":"Hallo","languages":[{"code":"de"}]}""");
        });
        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.RefreshAvailableLlmModelsAsync();
        var result = await sut.TranscribeWithLanguageHintsAsync(
            [0, 1, 2, 3],
            ["de", "en"],
            translate: false,
            prompt: "TypeWhisper",
            CancellationToken.None);

        Assert.Equal([snapshotModel], sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal(snapshotModel, sut.SelectedModelId);
        Assert.Equal("Hallo", result.Text);
        Assert.Equal(snapshotModel, fields.Single(field => field.Name == "model").Value);
        Assert.Equal(
            ["de", "en"],
            fields.Where(field => field.Name == "languages[]").Select(field => field.Value).ToArray());
        Assert.DoesNotContain(fields, field => field.Name == "language");
    }

    [Fact]
    public void GPTTranscribeResponseParser_AcceptsNewAndLegacyLanguageShapes()
    {
        var current = OpenAiTranscriptionClient.ParseTranscriptionResponse(
            """{"text":"Bonjour","languages":[{"code":"fr"}]}""");
        var legacy = OpenAiTranscriptionClient.ParseTranscriptionResponse(
            """{"text":"Hello","language":"en"}""");

        Assert.Equal("fr", current.DetectedLanguage);
        Assert.Equal("en", legacy.DetectedLanguage);
    }

    [Fact]
    public async Task GPTTranscribe_RejectsTranslationBeforeSendingRequest()
    {
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonResponse("{}"));
        });
        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-test";
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync(
                [0, 1, 2, 3],
                "de",
                translate: true,
                prompt: null,
                CancellationToken.None));

        Assert.Contains("does not support translation", exception.Message);
        Assert.Equal(0, requests);
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
                null,
                { "id": "whisper-1", "owned_by": "openai" },
                { "id": "gpt-transcribe", "owned_by": "openai" },
                { "id": "gpt-4o-mini-transcribe", "owned_by": "openai" },
                { "id": "gpt-4o-mini-transcribe-2025-03-20", "owned_by": "openai" },
                { "id": "gpt-4o-transcribe-diarize", "owned_by": "openai" },
                { "id": "gpt-live-transcribe", "owned_by": "openai" },
                { "id": "gpt-live-transcribe-2026-07-28", "owned_by": "openai" },
                { "id": "gpt-realtime-whisper", "owned_by": "openai" },
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
        host.SetSetting("selectedModel", "whisper-1");
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-4.1-mini", "o4-mini"], models.Select(m => m.Id).ToArray());
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("gpt-4.1-mini", sut.SelectedLlmModelId);
        Assert.Equal("gpt-4.1-mini", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal("whisper-1", sut.SelectedModelId);
        Assert.Equal(
            [
                "gpt-transcribe",
                "whisper-1",
                "gpt-4o-mini-transcribe",
                "gpt-4o-mini-transcribe-2025-03-20",
                "gpt-live-transcribe",
                "gpt-live-transcribe-2026-07-28",
                "gpt-realtime-whisper",
            ],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.DoesNotContain(
            sut.TranscriptionModels,
            model => model.Id.Contains("diarize", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://api.openai.com/v1/models", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("sk-live", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);

        var cachedModels = host.GetSetting<List<OpenAiFetchedModel>>("fetchedLLMModels");
        Assert.NotNull(cachedModels);
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], cachedModels.Select(m => m.Id).ToArray());

        var cachedTranscriptionModels =
            host.GetSetting<List<OpenAiFetchedModel>>("fetchedTranscriptionModels");
        Assert.NotNull(cachedTranscriptionModels);
        Assert.Equal(7, cachedTranscriptionModels.Count);
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_KeepsCachedCatalogsWhenApiRequestFails()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";
        host.SetSetting(
            "fetchedLLMModels",
            new List<OpenAiFetchedModel> { new("gpt-4.1-mini", "openai") });
        host.SetSetting(
            "fetchedTranscriptionModels",
            new List<OpenAiFetchedModel> { new("whisper-1", "openai") });
        host.SetSetting("selectedModel", "whisper-1");

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync();

        Assert.Empty(models);
        Assert.Equal(["gpt-4.1-mini"], sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Equal(["whisper-1"], sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal("whisper-1", sut.SelectedModelId);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_QueriesAccountSpecificChatGptCatalog()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""
            {
              "models": [
                null,
                {
                  "slug": "gpt-5.6-sol",
                  "display_name": "GPT-5.6-Sol",
                  "visibility": "list",
                  "priority": 1,
                  "available_in_plans": ["plus", "pro"]
                },
                {
                  "slug": "gpt-5.6-luna",
                  "display_name": "GPT-5.6-Luna",
                  "visibility": "list",
                  "priority": 2,
                  "available_in_plans": ["pro"]
                },
                {
                  "slug": "gpt-5.5",
                  "display_name": "GPT-5.5",
                  "visibility": "list",
                  "priority": 7,
                  "available_in_plans": []
                },
                {
                  "slug": "codex-auto-review",
                  "display_name": "Codex Auto Review",
                  "visibility": "hide",
                  "priority": 40
                }
              ]
            }
            """));
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("selectedLLMModel", "stale-model");
        host.SetSetting("oauthAccountID", "acct_123");
        host.SetSetting("oauthPlanType", "plus");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-5.6-sol", "gpt-5.5"], models.Select(model => model.Id).ToArray());
        Assert.Equal("GPT-5.6-Sol", models.First().DisplayName);
        Assert.Equal("gpt-5.6-sol", sut.SelectedLlmModelId);
        Assert.Equal("gpt-5.6-sol", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/models?client_version=1.1.1",
            capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer access-token", capturedRequest?.Headers.Authorization?.ToString());
        Assert.Equal(
            "acct_123",
            capturedRequest?.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("typewhisper", capturedRequest?.Headers.GetValues("originator").Single());
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);

        var cachedModels =
            host.GetSetting<List<OpenAiChatGptModel>>("fetchedChatGPTModels");
        Assert.NotNull(cachedModels);
        Assert.Equal(["gpt-5.6-sol", "gpt-5.5"], cachedModels.Select(model => model.Slug).ToArray());
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_RefreshesExpiredChatGptTokenBeforeCatalogRequest()
    {
        var requestedUris = new List<string>();
        var handler = new CapturingHandler((request, body) =>
        {
            requestedUris.Add(request.RequestUri!.ToString());
            if (request.RequestUri!.Host == "auth.openai.com")
            {
                Assert.Contains("grant_type=refresh_token", body);
                Assert.Contains("refresh_token=old-refresh-token", body);
                return Task.FromResult(JsonResponse("""
                {
                  "access_token": "new-access-token",
                  "refresh_token": "new-refresh-token",
                  "expires_in": 3600
                }
                """));
            }

            Assert.Equal("Bearer new-access-token", request.Headers.Authorization?.ToString());
            return Task.FromResult(JsonResponse("""
            {
              "models": [
                {
                  "slug": "gpt-5.6-sol",
                  "display_name": "GPT-5.6-Sol",
                  "visibility": "list",
                  "priority": 1
                }
              ]
            }
            """));
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthAccountID", "acct_123");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddMinutes(-1));
        host.Secrets["oauth-access-token"] = "expired-access-token";
        host.Secrets["oauth-refresh-token"] = "old-refresh-token";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync();

        Assert.Equal(
            [
                "https://auth.openai.com/oauth/token",
                "https://chatgpt.com/backend-api/codex/models?client_version=1.1.1",
            ],
            requestedUris);
        Assert.Equal(["gpt-5.6-sol"], models.Select(model => model.Id).ToArray());
        Assert.Equal("new-access-token", host.Secrets["oauth-access-token"]);
        Assert.Equal("new-refresh-token", host.Secrets["oauth-refresh-token"]);
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
        Assert.False(sut.SupportsDictionaryTerms);
        Assert.False(sut.SupportsStreaming);
        Assert.Equal("gpt-5.5", sut.SupportedModels.First().Id);

        var transcriptionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync(
                [0, 1, 2, 3],
                "de",
                translate: false,
                prompt: null,
                CancellationToken.None));
        var ttsError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hallo", "de"), CancellationToken.None));

        Assert.Contains("API key", transcriptionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API key", ttsError.Message, StringComparison.OrdinalIgnoreCase);
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
