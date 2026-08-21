using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GeminiPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.Join(
            RepositoryRoot(),
            "plugins",
            "TypeWhisper.Plugin.Gemini",
            "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        using var sut = new GeminiPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void SupportedModels_UsesCurrentAliasesWithExplicitDefault()
    {
        using var sut = new GeminiPlugin();

        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-flash-latest", "gemini-pro-latest"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Equal(GeminiPlugin.DefaultModel, Assert.Single(sut.SupportedModels, model => model.IsRecommended).Id);
    }

    [Theory]
    [InlineData("models/gemini-3.7-flash", true)]
    [InlineData("gemini-3.1-pro-preview", true)]
    [InlineData("gemma-4-31b-it", true)]
    [InlineData("gemini-3.1-flash-image", false)]
    [InlineData("gemini-embedding-2", false)]
    [InlineData("gemini-3.1-flash-tts-preview", false)]
    [InlineData("gemini-3.1-flash-live-preview", false)]
    [InlineData("gemini-omni-flash", false)]
    [InlineData("gemini-omni-flash-preview", false)]
    [InlineData("gemini-robotics-er-2-preview", false)]
    [InlineData("deep-research-preview", false)]
    [InlineData("veo-3.1-generate-preview", false)]
    public void IsCompatibleChatModelId_FiltersCatalog(string modelId, bool expected)
    {
        Assert.Equal(expected, GeminiPlugin.IsCompatibleChatModelId(modelId));
    }

    [Fact]
    public async Task ActivateAsync_RestoresNormalizedCachedModels()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = " gemini-key ";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("models/gemma-4-31b-it", " Gemma 4 31B IT "),
            new("models/gemini-flash-latest", "Gemini Flash Latest"),
            new("models/gemini-3.1-flash-image", "Nano Banana"),
        });
        using var sut = new GeminiPlugin();

        await sut.ActivateAsync(host);

        Assert.True(sut.IsAvailable);
        Assert.Equal(
            ["gemini-flash-latest", "gemma-4-31b-it"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Equal("Gemma 4 31B IT", sut.SupportedModels.Last().DisplayName);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_UsesCompatibleEndpointAndFiltersSparseResults()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    null,
                    { "id": null, "display_name": "Missing ID" },
                    { "object": "model", "display_name": "No ID" },
                    { "id": "models/gemini-3.7-flash", "display_name": "Gemini 3.7 Flash" },
                    { "id": "models/gemma-4-31b-it", "display_name": "Gemma 4 31B IT" },
                    { "id": "gemini-3.7-flash", "display_name": "Duplicate" },
                    { "id": "models/gemini-3.1-flash-image", "display_name": "Nano Banana" },
                    { "id": "models/gemini-embedding-2", "display_name": "Embedding" },
                    { "id": "models/veo-3.1-generate-preview", "display_name": "Veo" }
                  ]
                }
                """);
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchLlmModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(
            ["gemini-3.7-flash", "gemma-4-31b-it"],
            models!.Select(model => model.Id).ToArray());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/models", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer gemini-key", capturedRequest?.Headers.Authorization?.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task FetchLlmModelsAsync_RejectsCatalogWithoutDataArray(string json)
    {
        var handler = new CapturingHandler((_, _) => JsonResponse(json));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
        });
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.FetchLlmModelsAsync();

        Assert.Null(result);
        Assert.Contains(sut.FetchedLlmModels, model => model.Id == "gemini-3.7-flash");
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Warning
            && entry.Message.Contains("data array", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_AcceptsExplicitEmptyCatalog()
    {
        var handler = new CapturingHandler((_, _) => JsonResponse("""{"data":[]}"""));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.FetchLlmModelsAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SetFetchedLlmModels_KeepsCurrentFetchedFlashModelFirstAndNotifiesHost()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetFetchedLlmModelsAsync(
        [
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new("gemma-4-26b-a4b-it", "Gemma 4 26B MoE IT"),
        ]);

        Assert.Equal(
            ["gemini-3.7-flash", "gemma-4-26b-a4b-it"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.True(sut.SupportedModels[0].IsRecommended);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        Assert.Equal(
            ["gemini-3.7-flash", "gemma-4-26b-a4b-it"],
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!
                .Select(model => model.Id)
                .ToArray());
    }

    [Fact]
    public async Task SetFetchedLlmModels_DoesNotPersistOrNotifyForUnchangedCatalog()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetFetchedLlmModelsAsync([new("gemini-3.7-flash", "Gemini 3.7 Flash")]);
        await sut.SetFetchedLlmModelsAsync([new("models/gemini-3.7-flash", "Gemini 3.7 Flash")]);

        Assert.Equal(1, host.SetSettingCount);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_ClearsCatalogOwnedByPreviousKey()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "first-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
        });
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.ResetTracking();

        await sut.SetApiKeyAsync("second-key");

        Assert.Equal("second-key", host.Secrets["api-key"]);
        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-flash-latest", "gemini-pro-latest"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Empty(host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!);
        Assert.Equal(1, host.SetSettingCount);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_LeavesStateUnchangedWhenSecretWriteFails()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "first-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
        });
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.ResetTracking();
        host.StoreSecretException = new InvalidOperationException("secret write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync("second-key"));

        Assert.Equal("first-key", sut.ApiKey);
        Assert.Equal("first-key", host.Secrets["api-key"]);
        Assert.Contains(sut.FetchedLlmModels, model => model.Id == "gemini-3.7-flash");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_RestoresSecretWhenCatalogWriteFails()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "first-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
        });
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.ResetTracking();
        host.SetSettingExceptionAfterWrite = new InvalidOperationException("catalog write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync("second-key"));

        Assert.Equal("first-key", sut.ApiKey);
        Assert.Equal("first-key", host.Secrets["api-key"]);
        Assert.Contains(sut.FetchedLlmModels, model => model.Id == "gemini-3.7-flash");
        Assert.Contains(
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!,
            model => model.Id == "gemini-3.7-flash");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetFetchedLlmModelsAsync_LeavesStateUnchangedWhenSettingWriteFails()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.SetSettingExceptionAfterWrite = new InvalidOperationException("catalog write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetFetchedLlmModelsAsync(
            [new("gemini-3.7-flash", "Gemini 3.7 Flash")]));

        Assert.Empty(sut.FetchedLlmModels);
        Assert.Equal(GeminiPlugin.DefaultModel, sut.SupportedModels[0].Id);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ProcessAsync_UsesDocumentedGeminiChatEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"  refreshed result  "}}]}""");
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync(
            "Clean up technical dictation",
            "hello world",
            "gemini-3.7-flash",
            CancellationToken.None);

        Assert.Equal("refreshed result", result);
        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer gemini-key", capturedRequest?.Headers.Authorization?.ToString());

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ProcessAsync_OmitsGeminiReasoningEffortForGemmaModels()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "gemma-4-31b-it", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemma-4-31b-it", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task ProcessAsync_NormalizesModelsPrefixBeforeApplyingGeminiOptions()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "models/gemini-3.7-flash", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ProcessAsync_ClassifiesMalformedSuccessResponse()
    {
        var handler = new CapturingHandler((_, _) => JsonResponse("not-json"));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync(
            "system",
            "user",
            "gemini-3.7-flash",
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task ProcessAsync_UsesCuratedDefaultWhenModelIsBlank()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal(GeminiPlugin.DefaultModel, body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessAsync_UsesCurrentFetchedFlashModelWhenModelIsBlank()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-2.5-flash", "Gemini 2.5 Flash"),
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new("gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview"),
        });
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("gemini-3.7-flash", sut.SupportedModels[0].Id);
        Assert.True(sut.SupportedModels[0].IsRecommended);
    }

    [Fact]
    public async Task SupportedModels_OrdersFallbackFlashVersionsNumerically()
    {
        using var sut = new GeminiPlugin();

        await sut.SetFetchedLlmModelsAsync(
        [
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new("gemini-3.10-flash", "Gemini 3.10 Flash"),
            new("gemini-3.11-flash-preview", "Gemini 3.11 Flash Preview"),
        ]);

        Assert.Equal("gemini-3.10-flash", sut.SupportedModels[0].Id);
        Assert.True(sut.SupportedModels[0].IsRecommended);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_FailureKeepsCachedCatalog()
    {
        var handler = new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
        });
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.FetchLlmModelsAsync();

        Assert.Null(result);
        Assert.Contains(sut.SupportedModels, model => model.Id == "gemini-3.7-flash");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Warning
            && entry.Message.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateApiKeyAsync_ReturnsFalseForUnauthorizedResponse()
    {
        var handler = new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);

        var result = await sut.ValidateApiKeyAsync("invalid-key");

        Assert.False(result);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_RethrowsCallerCancellation()
    {
        using var response = JsonResponse("""{"data":[]}""");
        var handler = new BlockingHandler(response);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);
        using var cts = new CancellationTokenSource();

        var fetchTask = sut.FetchLlmModelsAsync(cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetchTask);

        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Debug
            && entry.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_DiscardsCatalogWhenApiKeyChangesInFlight()
    {
        var handler = new BlockingHandler(JsonResponse("""
            {"data":[{"id":"gemini-3.7-flash","display_name":"Gemini 3.7 Flash"}]}
            """));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "first-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-2.5-flash", "Gemini 2.5 Flash"),
        });
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var fetchTask = sut.FetchLlmModelsAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sut.SetApiKeyAsync("second-key");
        handler.Release();
        var result = await fetchTask;

        Assert.Null(result);
        Assert.Equal("second-key", sut.ApiKey);
        Assert.Empty(sut.FetchedLlmModels);
        Assert.Empty(host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Debug
            && entry.Message.Contains("previous API key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SetFetchedLlmModelsAsync_RejectsCatalogForPreviousApiKey()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "first-key";
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("second-key");

        var applied = await sut.SetFetchedLlmModelsAsync(
            [new("gemini-3.7-flash", "Gemini 3.7 Flash")],
            expectedApiKey: "first-key");

        Assert.False(applied);
        Assert.Empty(sut.FetchedLlmModels);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Debug
            && entry.Message.Contains("previous API key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenAvailabilityChanges()
    {
        var host = new TestPluginHostServices();
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" first-key ");
        await sut.SetApiKeyAsync("second-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.DoesNotContain("api-key", host.Secrets.Keys);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class BlockingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return response;
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly ConcurrentDictionary<string, JsonElement> _settings = [];
        private Exception? _storeSecretException;
        private Exception? _setSettingException;
        private Exception? _setSettingExceptionAfterWrite;
        private int _notifyCapabilitiesChangedCount;
        private int _setSettingCount;

        public ConcurrentDictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount => Volatile.Read(ref _notifyCapabilitiesChangedCount);
        public int SetSettingCount => Volatile.Read(ref _setSettingCount);
        public ConcurrentQueue<(PluginLogLevel Level, string Message)> Logs { get; } = [];
        public Exception? StoreSecretException
        {
            set => Interlocked.Exchange(ref _storeSecretException, value);
        }
        public Exception? SetSettingException
        {
            set => Interlocked.Exchange(ref _setSettingException, value);
        }
        public Exception? SetSettingExceptionAfterWrite
        {
            set => Interlocked.Exchange(ref _setSettingExceptionAfterWrite, value);
        }

        public Task StoreSecretAsync(string key, string value)
        {
            if (Interlocked.Exchange(ref _storeSecretException, null) is { } exception)
            {
                throw exception;
            }

            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);

        public Task DeleteSecretAsync(string key)
        {
            Secrets.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;

        public void SetSetting<T>(string key, T value)
        {
            if (Interlocked.Exchange(ref _setSettingException, null) is { } exception)
            {
                throw exception;
            }

            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);
            Interlocked.Increment(ref _setSettingCount);
            if (Interlocked.Exchange(ref _setSettingExceptionAfterWrite, null) is { } afterWriteException)
            {
                throw afterWriteException;
            }
        }

        public void ResetTracking()
        {
            Interlocked.Exchange(ref _notifyCapabilitiesChangedCount, 0);
            Interlocked.Exchange(ref _setSettingCount, 0);
            Logs.Clear();
        }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) => Logs.Enqueue((level, message));
        public void NotifyCapabilitiesChanged() => Interlocked.Increment(ref _notifyCapabilitiesChangedCount);
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

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx"))
                && Directory.Exists(Path.Join(directory.FullName, "plugins")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("TypeWhisper repository root not found.");
    }
}
