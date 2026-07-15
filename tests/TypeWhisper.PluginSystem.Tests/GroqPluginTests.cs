using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Groq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class GroqPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Groq", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new GroqPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesTranscriptionAndLlmCategories()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Groq", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Equal("transcription", manifest.Category);
        Assert.Equal(["transcription", "llm"], manifest.Categories);
    }

    [Fact]
    public void TranscriptionModels_RemainCuratedWhisperModels()
    {
        var sut = new GroqPlugin();

        var ids = sut.TranscriptionModels.Select(m => m.Id).ToArray();

        Assert.Equal(["whisper-large-v3", "whisper-large-v3-turbo"], ids);
    }

    [Theory]
    [InlineData("whisper-large-v3", false)]
    [InlineData("distil-whisper-large-v3-en", false)]
    [InlineData("meta-llama/llama-3.3-70b-versatile-tool-use-preview", false)]
    [InlineData("playai-tts", false)]
    [InlineData("orpheus-tts-1", false)]
    [InlineData("llama-guard-prompt-guard", false)]
    [InlineData("openai/gpt-oss-safeguard-20b", false)]
    [InlineData("llama-3.1-8b-instant", true)]
    [InlineData("openai/gpt-oss-120b", true)]
    public void IsLlmModel_FiltersGroqModelIds(string modelId, bool expected)
    {
        Assert.Equal(expected, GroqPlugin.IsLlmModel(modelId));
    }

    [Fact]
    public async Task ActivateAsync_RestoresFetchedModelsAndNormalizesStaleSelectedLlmModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";
        host.SetSetting("fetchedLlmModels", new List<FetchedLlmModel>
        {
            new("openai/gpt-oss-120b", "OpenAI"),
            new("llama-3.1-8b-instant", "Meta"),
        });
        host.SetSetting("selectedLlmModel", "whisper-large-v3");

        var sut = new GroqPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("llama-3.1-8b-instant", sut.SelectedLlmModelId);
        Assert.Equal(
            ["llama-3.1-8b-instant", "openai/gpt-oss-120b"],
            sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("llama-3.1-8b-instant", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_UsesFallbackSelectedLlmModelWhenUnset()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        var sut = new GroqPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(sut.SupportedModels.First().Id, sut.SelectedLlmModelId);
        Assert.Equal(sut.SupportedModels.First().Id, host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_FiltersAndSortsGroqResults()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer groq-key", request.Headers.Authorization?.ToString());

            return JsonResponse("""
                {
                  "data": [
                    { "id": "whisper-large-v3", "owned_by": "OpenAI" },
                    { "id": "openai/gpt-oss-120b", "owned_by": "OpenAI" },
                    { "id": "llama-3.1-8b-instant", "owned_by": "Meta" },
                    { "id": "orpheus-tts-1", "owned_by": "Groq" }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchLlmModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(
            ["llama-3.1-8b-instant", "openai/gpt-oss-120b"],
            models!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task TranscribeAsync_UsesCompressedUploadAndLongTimeoutClient()
    {
        string? transcriptionBody = null;
        var normalHandler = new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Normal HTTP client should not be used for transcription."));
        var transcriptionHandler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.Equal("Bearer groq-key", request.Headers.Authorization?.ToString());

            transcriptionBody = body;
            Assert.NotNull(body);
            AssertMultipartToken(body, "filename", "audio.m4a");
            Assert.Contains("Content-Type: audio/mp4", body);
            Assert.DoesNotContain("filename=\"audio.wav\"", body);
            Assert.DoesNotContain("filename=audio.wav", body);

            return JsonResponse("""{"text":"ok","language":"en","duration":1.0}""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        using var normalHttpClient = new HttpClient(normalHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using var transcriptionHttpClient = GroqPlugin.CreateTranscriptionHttpClient(transcriptionHandler);
        var sut = new GroqPlugin(
            normalHttpClient,
            transcriptionHttpClient,
            _ => new GroqTranscriptionUpload([1, 2, 3], "audio.m4a", "audio/mp4"));
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([4, 5, 6], "en", false, null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), transcriptionHttpClient.Timeout);
        Assert.NotNull(transcriptionBody);
        Assert.Equal("ok", result.Text);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(COMException))]
    public async Task TranscribeAsync_EncoderFailureFallsBackToWavUpload(Type compressionExceptionType)
    {
        string? transcriptionBody = null;
        var normalHandler = new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Normal HTTP client should not be used for transcription."));
        var transcriptionHandler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.Equal("Bearer groq-key", request.Headers.Authorization?.ToString());

            transcriptionBody = body;
            Assert.NotNull(body);
            AssertMultipartToken(body, "filename", "audio.wav");
            Assert.Contains("Content-Type: audio/wav", body);
            Assert.DoesNotContain("filename=\"audio.m4a\"", body);
            Assert.DoesNotContain("filename=audio.m4a", body);

            return JsonResponse("""{"text":"fallback","language":"en","duration":1.0}""");
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        using var httpClient = new HttpClient(normalHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using var transcriptionHttpClient = GroqPlugin.CreateTranscriptionHttpClient(transcriptionHandler);
        var compressionException = CreateCompressionFailure(compressionExceptionType);
        var sut = new GroqPlugin(
            httpClient,
            transcriptionHttpClient,
            _ => throw compressionException);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], null, false, null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), transcriptionHttpClient.Timeout);
        Assert.NotNull(transcriptionBody);
        Assert.Equal("fallback", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_CanceledBeforeCompressionSkipsEncoder()
    {
        var handler = new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Upload should not be attempted."));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";
        var encoderCalled = false;

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var transcriptionHttpClient = GroqPlugin.CreateTranscriptionHttpClient(handler);
        var sut = new GroqPlugin(
            httpClient,
            transcriptionHttpClient,
            _ =>
            {
                encoderCalled = true;
                return new GroqTranscriptionUpload([1], "audio.m4a", "audio/mp4");
            });
        await sut.ActivateAsync(host);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.TranscribeAsync([1, 2, 3], null, false, null, cts.Token));

        Assert.False(encoderCalled);
    }

    [Fact]
    public async Task ProcessAsync_UsesSelectedLlmModelWhenCallerDoesNotOverride()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing request body."));
            Assert.Equal("openai/gpt-oss-120b", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.TryGetProperty("reasoning_format", out _));

            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "done"
                      }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";
        host.SetSetting("selectedLlmModel", "openai/gpt-oss-120b");

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task ProcessAsync_HidesReasoningForQwenModels()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing request body."));
            Assert.Equal("hidden", doc.RootElement.GetProperty("reasoning_format").GetString());

            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "final answer"
                      }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "qwen/qwen3.6-27b", CancellationToken.None);

        Assert.Equal("final answer", result);
    }

    [Fact]
    public async Task ProcessAsync_UsesFallbackLlmModelWhenSelectionMissing()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing request body."));
            Assert.Equal("llama-3.3-70b-versatile", doc.RootElement.GetProperty("model").GetString());

            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "fallback"
                      }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("fallback", result);
    }

    [Fact]
    public async Task SetApiKeyAsync_DoesNotNotifyWhenPrefilledValueIsWrittenBack()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "groq-key";

        var sut = new GroqPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("groq-key");

        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new GroqPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("groq-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static Exception CreateCompressionFailure(Type exceptionType)
    {
        if (exceptionType == typeof(IOException))
            return new IOException("codec unavailable");

        if (exceptionType == typeof(InvalidOperationException))
            return new InvalidOperationException("codec unavailable");

        if (exceptionType == typeof(COMException))
            return new COMException("codec unavailable");

        throw new ArgumentOutOfRangeException(
            nameof(exceptionType),
            exceptionType,
            "Unsupported compression exception type.");
    }

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

    private static void AssertMultipartToken(string body, string name, string value)
    {
        Assert.True(
            body.Contains($"{name}=\"{value}\"", StringComparison.Ordinal)
            || body.Contains($"{name}={value}", StringComparison.Ordinal),
            $"Multipart body did not contain {name}={value}:{Environment.NewLine}{body}");
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
