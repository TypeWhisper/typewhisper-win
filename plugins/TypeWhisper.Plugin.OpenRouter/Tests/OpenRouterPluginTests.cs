using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.OpenRouter;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenRouterPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new OpenRouterPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void SettingsViewXaml_ExposesModelSearchCreditsAndTemperatureControls()
    {
        var xaml = File.ReadAllText(GetRepoPath(
            "plugins", "TypeWhisper.Plugin.OpenRouter", "OpenRouterSettingsView.xaml"));

        Assert.Contains("SearchBox", xaml);
        Assert.Contains("RefreshButton", xaml);
        Assert.DoesNotContain("TranscriptionModelPicker", xaml);
        Assert.Contains("LlmModelPicker", xaml);
        Assert.Contains("CreditsText", xaml);
        Assert.Contains("TemperatureModePicker", xaml);
        Assert.Contains("TemperatureSlider", xaml);
    }

    [Fact]
    public void ProjectFile_DoesNotBundleHostPluginSdk()
    {
        var project = File.ReadAllText(GetRepoPath(
            "plugins", "TypeWhisper.Plugin.OpenRouter", "TypeWhisper.Plugin.OpenRouter.csproj"));

        Assert.Contains("TypeWhisper.PluginSDK.csproj\" Private=\"false\"", project);
        Assert.Contains("$(TargetDir)TypeWhisper.PluginSDK.dll", project);
        Assert.Contains("$(PluginOutputDir)TypeWhisper.PluginSDK.dll", project);
    }

    [Fact]
    public async Task ActivateAsync_ExposesOpenRouterAsTranscriptionEngineWithDefaultModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.IsAssignableFrom<ITranscriptionEnginePlugin>(sut);
        Assert.Equal("openrouter", sut.ProviderId);
        Assert.Equal("OpenRouter", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("openai/whisper-large-v3-turbo", sut.SelectedModelId);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "openai/gpt-4o-mini-transcribe");
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "openai/whisper-large-v3-turbo");
    }

    [Fact]
    public async Task ActivateAsync_RestoresFetchedTranscriptionModelsAndNormalizesStaleSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("fetchedTranscriptionModels", new List<OpenRouterFetchedModel>
        {
            new("z/stt", "Zulu STT", "0.000002", "0"),
            new("a/stt", "Alpha STT", "0", "0"),
        });
        host.SetSetting("selectedTranscriptionModel", "missing/stt");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("a/stt", sut.SelectedModelId);
        Assert.Equal(["a/stt", "z/stt"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("a/stt", host.GetSetting<string>("selectedTranscriptionModel"));
    }

    [Fact]
    public async Task ActivateAsync_UsesOpenRouterFreeAsDefaultWhenSelectionUnset()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal("openrouter/free", sut.SupportedModels.First().Id);
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_MigratesLegacyFallbackDefaultToOpenRouterFree()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "openai/gpt-4o");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_MigratesUnmarkedSavedSelectionToOpenRouterFree()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "openrouter/owl-alpha");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_RestoresFetchedModelsAndNormalizesStaleSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("fetchedModels", new List<OpenRouterFetchedModel>
        {
            new("z/model", "Z Model", "0.000002", "0.000003"),
            new("a/model", "A Model", "0", "0"),
        });
        host.SetSetting("selectedLlmModel", "missing/model");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal(["openrouter/free", "a/model", "z/model"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_PreservesMarkedUserSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "selected/model");
        host.SetSetting("userSelectedLlmModel", true);

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("selected/model", sut.SelectedLlmModelId);
        Assert.Equal("selected/model", host.GetSetting<string>("selectedLlmModel"));
    }

    [Theory]
    [InlineData("text->text", "openai/gpt-4o", true)]
    [InlineData("text+image->text", "anthropic/claude-sonnet-4", true)]
    [InlineData("text->image", "image/model", false)]
    [InlineData("", "openai/gpt-4o", true)]
    [InlineData("", "openai/text-embedding-3-small", false)]
    [InlineData("", "openai/whisper-1", false)]
    [InlineData("", "stability/stable-diffusion-xl", false)]
    public void IsTextLlm_FiltersByModalityAndModelId(string modality, string modelId, bool expected)
    {
        Assert.Equal(expected, OpenRouterPlugin.IsTextLlm(modality, modelId));
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesOpenRouterAuthKeyEndpoint()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/auth/key", request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""{ "data": { "limit_remaining": 12.5 } }""");
        });

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("openrouter-key"));
    }

    [Fact]
    public async Task FetchModelsAsync_FiltersTextModelsSortsByNameAndKeepsPricing()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {
                  "data": [
                    {
                      "id": "openai/text-embedding-3-small",
                      "name": "Embedding",
                      "pricing": { "prompt": "0.00000002", "completion": "0" },
                      "architecture": { "modality": "text->text" }
                    },
                    {
                      "id": "z/model",
                      "name": "Zulu",
                      "pricing": { "prompt": "0.000002", "completion": "0.000003" },
                      "architecture": { "modality": "text->text" }
                    },
                    {
                      "id": "a/model",
                      "name": "Alpha",
                      "pricing": { "prompt": "0", "completion": "0" },
                      "architecture": { "modality": "text+image->text" }
                    },
                    {
                      "id": "image/model",
                      "name": "Image",
                      "pricing": { "prompt": "0", "completion": "0" },
                      "architecture": { "modality": "text->image" }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchModelsAsync();

        Assert.Equal(["openrouter/free", "a/model", "z/model"], models.Select(m => m.Id).ToArray());
        Assert.Equal("OpenRouter: Free Models Router (free)", models[0].Name);
        Assert.Equal("Free", models[0].FormattedPricing("Free"));
        Assert.Equal("Alpha", models[1].Name);
        Assert.Equal("Free", models[1].FormattedPricing("Free"));
        Assert.Equal("$2.00/$3.00 per 1M", models[2].FormattedPricing("Free"));
    }

    [Fact]
    public void FormattedPricing_TreatsEffectivelyZeroPricesAsFree()
    {
        var sut = new OpenRouterFetchedModel(
            "tiny/free",
            "Tiny Free",
            "0.0000000000000001",
            "0");

        Assert.Equal("Free", sut.FormattedPricing("Free"));
    }

    [Fact]
    public async Task FetchTranscriptionModelsAsync_UsesOutputModalitiesFilterAndSortsModels()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/models?output_modalities=transcription", request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {
                  "data": [
                    {
                      "id": "openai/whisper-large-v3-turbo",
                      "name": "OpenAI: Whisper Large V3 Turbo",
                      "pricing": { "prompt": "0.000001", "completion": "0" },
                      "architecture": { "modality": "audio->text" }
                    },
                    {
                      "id": "google/chirp-3",
                      "name": "Google: Chirp 3",
                      "pricing": { "prompt": "0", "completion": "0" },
                      "architecture": { "modality": "audio->text" }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchTranscriptionModelsAsync();

        Assert.Equal(["google/chirp-3", "openai/whisper-large-v3-turbo"], models.Select(m => m.Id).ToArray());
        Assert.Equal("Google: Chirp 3", models[0].Name);
        Assert.Equal("$1.00/$0.00 per 1M", models[1].FormattedPricing("Free"));
    }

    [Fact]
    public async Task FetchCreditsAsync_ParsesLimitMinusUsage()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/auth/key", request.RequestUri?.ToString());
            return JsonResponse("""{ "data": { "limit": 20.0, "usage": 7.25 } }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Equal(12.75, await sut.FetchCreditsAsync());
    }

    [Fact]
    public async Task FetchCreditsAsync_ParsesLimitRemaining()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "data": { "limit_remaining": 4.5 } }"""));

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Equal(4.5, await sut.FetchCreditsAsync());
    }

    [Fact]
    public async Task TranscribeAsync_PostsBase64WavToOpenRouterTranscriptionsEndpoint()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("openai/whisper-large-v3-turbo", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("de", doc.RootElement.GetProperty("language").GetString());
            var audio = doc.RootElement.GetProperty("input_audio");
            Assert.Equal("wav", audio.GetProperty("format").GetString());
            Assert.Equal(Convert.ToBase64String([1, 2, 3]), audio.GetProperty("data").GetString());

            return JsonResponse("""{ "text": "Hallo Welt", "usage": { "seconds": 1.25 } }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "de", translate: false, prompt: "ignored", CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsAutoLanguageAndRejectsTranslation()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.NotNull(request);
            Assert.False(doc.RootElement.TryGetProperty("language", out var _));
            return JsonResponse("""{ "text": "auto", "usage": { "seconds": 0.5 } }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.TranscribeAsync([1], "auto", translate: false, prompt: null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1], "en", translate: true, prompt: null, CancellationToken.None));
        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public async Task ProcessAsync_UsesOpenRouterFreeDefaultWhenCallerDoesNotOverride()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("openrouter/free", doc.RootElement.GetProperty("model").GetString());
            return JsonResponse("""{ "choices": [ { "message": { "content": "default" } } ] }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("default", result);
    }

    [Fact]
    public async Task ProcessAsync_UsesSelectedModelAndOmitsTemperatureForProviderDefault()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri?.ToString());
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("selected/model", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
            return JsonResponse("""{ "choices": [ { "message": { "content": "done" } } ] }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "selected/model");
        host.SetSetting("userSelectedLlmModel", true);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task ProcessAsync_UsesCallerModelAndCustomTemperature()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("override/model", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal(1.2, doc.RootElement.GetProperty("temperature").GetDouble(), precision: 3);
            return JsonResponse("""{ "choices": [ { "message": { "content": "custom" } } ] }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("llmTemperatureMode", "custom");
        host.SetSetting("llmTemperatureValue", 1.2);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "override/model", CancellationToken.None);

        Assert.Equal("custom", result);
    }

    private static JsonElement LoadManifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GetRepoPath(
            "plugins", "TypeWhisper.Plugin.OpenRouter", "manifest.json")));
        return doc.RootElement.Clone();
    }

    private static string GetRepoPath(params string[] parts)
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        return Path.GetFullPath(Path.Join(
            [basePath, "..", "..", "..", "..", "..", .. parts]));
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
