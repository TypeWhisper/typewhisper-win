using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiCompatiblePluginTests
{
    [Fact]
    public void Manifest_AdvertisesTranscriptionAndLlmCategories()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.OpenAiCompatible", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Equal("transcription", manifest.Category);
        Assert.Equal(["transcription", "llm"], manifest.Categories);
    }

    [Fact]
    public async Task ActivateAsync_MigratesLegacySettingsIntoDefaultProfileWithoutPersistingSecret()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "legacy-token";
        host.SetSetting("baseUrl", "https://legacy.example/v1/");
        host.SetSetting("selectedModel", "whisper-legacy");
        host.SetSetting("selectedLlmModel", "gpt-legacy");
        host.SetSetting("fetchedModels", JsonSerializer.Serialize(new[]
        {
            new FetchedModel("whisper-legacy", "legacy"),
            new FetchedModel("gpt-legacy", "legacy")
        }));

        var sut = new OpenAiCompatiblePlugin();
        await sut.ActivateAsync(host);

        var profile = Assert.Single(sut.Profiles);
        Assert.Equal(OpenAiCompatiblePlugin.DefaultProfileId, profile.Id);
        Assert.Equal("OpenAI Compatible", profile.Name);
        Assert.Equal("https://legacy.example", profile.BaseUrl);
        Assert.Equal("whisper-legacy", profile.SelectedModelId);
        Assert.Equal("gpt-legacy", profile.SelectedLlmModelId);
        Assert.Equal(["gpt-legacy", "whisper-legacy"], profile.FetchedModels.Select(model => model.Id).Order().ToArray());
        Assert.Equal("legacy-token", sut.GetApiKey());

        var persistedProfiles = host.GetRawSettingJson("profiles");
        Assert.Contains("whisper-legacy", persistedProfiles);
        Assert.DoesNotContain("legacy-token", persistedProfiles);
        Assert.Equal("https://legacy.example", host.GetSetting<string>("baseUrl"));
        Assert.Equal("whisper-legacy", host.GetSetting<string>("selectedModel"));
        Assert.Equal("gpt-legacy", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task AdditionalProfileRolesExposeIndependentSelectionIdsAndPersistSecretSeparately()
    {
        var host = new TestPluginHostServices();
        var sut = new OpenAiCompatiblePlugin();
        await sut.ActivateAsync(host);

        var profile = sut.AddProfile("Local Gateway");
        sut.SetBaseUrl("https://local.example/v1", profile.Id);
        sut.SelectModelForProfile(profile.Id, "whisper-local");
        sut.SelectLlmModelForProfile(profile.Id, "gpt-local");
        await sut.SetApiKeyAsync("local-token", profile.Id);

        var transcriptionRole = Assert.Single(((IAdditionalTranscriptionEnginesProvider)sut).AdditionalTranscriptionEngines);
        var llmRole = Assert.Single(((IAdditionalLlmProvidersProvider)sut).AdditionalLlmProviders);

        Assert.StartsWith("openai-compatible-", profile.Id);
        Assert.DoesNotContain(":", profile.Id);
        Assert.Equal(profile.Id, transcriptionRole.GetTranscriptionSelectionId());
        Assert.Equal(profile.Id, llmRole.GetLlmSelectionId());
        Assert.Equal("com.typewhisper.openai-compatible", transcriptionRole.PluginId);
        Assert.Equal("Local Gateway", transcriptionRole.ProviderDisplayName);
        Assert.Equal("Local Gateway", llmRole.ProviderName);
        Assert.Equal("whisper-local", transcriptionRole.SelectedModelId);
        Assert.Equal("gpt-local", llmRole.SupportedModels.Single().Id);
        Assert.Equal("local-token", host.Secrets[$"api-key.{profile.Id}"]);
        Assert.DoesNotContain("local-token", host.GetRawSettingJson("profiles"));

        Assert.True(sut.RenameProfile(profile.Id, "Renamed Gateway"));
        Assert.Equal("Renamed Gateway", transcriptionRole.ProviderDisplayName);

        Assert.True(await sut.DeleteProfileAsync(profile.Id));
        Assert.Empty(((IAdditionalTranscriptionEnginesProvider)sut).AdditionalTranscriptionEngines);
        Assert.Empty(((IAdditionalLlmProvidersProvider)sut).AdditionalLlmProviders);
        Assert.False(host.Secrets.ContainsKey($"api-key.{profile.Id}"));
    }

    [Fact]
    public async Task TranscriptionAndLlmRequestsUseTheSelectedProfileUrlSecretAndModel()
    {
        var requests = new List<CapturedRequest>();
        var handler = new CapturingHandler((request, body) =>
        {
            requests.Add(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            if (request.RequestUri!.AbsolutePath.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "text": "transcribed",
                      "language": "en",
                      "duration": 1.5
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "processed"
                      }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        sut.SetBaseUrl("https://default.example/v1");
        sut.SelectModel("whisper-default");
        sut.SelectLlmModel("gpt-default");
        await sut.SetApiKeyAsync("default-token");

        var profile = sut.AddProfile("Profile");
        sut.SetBaseUrl("https://profile.example/v1", profile.Id);
        sut.SelectModelForProfile(profile.Id, "whisper-profile");
        sut.SelectLlmModelForProfile(profile.Id, "gpt-profile");
        await sut.SetApiKeyAsync("profile-token", profile.Id);

        var profileTranscription = ((IAdditionalTranscriptionEnginesProvider)sut).AdditionalTranscriptionEngines.Single();
        var profileLlm = ((IAdditionalLlmProvidersProvider)sut).AdditionalLlmProviders.Single();

        await sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None);
        await profileTranscription.TranscribeAsync([4, 5, 6], "de", translate: false, prompt: "terms", CancellationToken.None);
        await sut.ProcessAsync("system", "user", "", CancellationToken.None);
        await profileLlm.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Contains(requests, request =>
            request.Url == "https://default.example/v1/audio/transcriptions"
            && request.Authorization == "Bearer default-token"
            && request.Body?.Contains("whisper-default", StringComparison.Ordinal) == true);
        Assert.Contains(requests, request =>
            request.Url == "https://profile.example/v1/audio/transcriptions"
            && request.Authorization == "Bearer profile-token"
            && request.Body?.Contains("whisper-profile", StringComparison.Ordinal) == true);
        Assert.Contains(requests, request =>
            request.Url == "https://default.example/v1/chat/completions"
            && request.Authorization == "Bearer default-token"
            && request.Body?.Contains("\"model\":\"gpt-default\"", StringComparison.Ordinal) == true);
        Assert.Contains(requests, request =>
            request.Url == "https://profile.example/v1/chat/completions"
            && request.Authorization == "Bearer profile-token"
            && request.Body?.Contains("\"model\":\"gpt-profile\"", StringComparison.Ordinal) == true);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed record CapturedRequest(string Url, string? Authorization, string? Body);

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
        public List<string> Logs { get; } = [];
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

        public string GetRawSettingJson(string key) =>
            _settings.TryGetValue(key, out var value) ? value.GetRawText() : "";

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) => Logs.Add(message);
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
