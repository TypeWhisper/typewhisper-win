using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Cerebras;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

public partial class CerebrasTests
{
    private const string Answer = """{"choices":[{"finish_reason":"stop","message":{"content":" Hello team. ","reasoning":"private reasoning"}}]}""";
    private static readonly Dictionary<string, string> Empty = [];

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
    private static CerebrasPlugin Plugin(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) =>
        new(new HttpClient(new Handler(send ?? ((_, _) => throw new Exception("Unexpected network request.")))));
    private static async Task Configure(CerebrasPlugin plugin, TestPluginHostServices host)
    {
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("fixture-key");
    }

    [Fact]
    public async Task SettingsAreOfflineLocalizedAndExposeOnlyTextCapabilities()
    {
        using var plugin = Plugin();
        await plugin.ActivateAsync(new TestPluginHostServices());
        Assert.False(plugin.IsAvailable);
        Assert.Equal(new[] { "gpt-oss-120b", "qwen-3.8-27b" }, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(4, plugin.TextSettings.Count);
        Assert.Equal(2, plugin.SettingsActions.Count);
        Assert.Equal("providerDefault", Field(plugin, "llmTemperatureMode"));
        Assert.Equal("custom", Assert.Single(plugin.TextSettings.Single(f => f.Id == "llmTemperatureValue").VisibleWhen!.Values));
        Assert.True(plugin.SupportsRequestHedging);
        Assert.DoesNotContain(typeof(ITranscriptionEnginePlugin), plugin.GetType().GetInterfaces());
        Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase");
    }

    [Theory]
    [InlineData("", "gpt-oss-120b", true)]
    [InlineData("qwen-3.8-27b", "qwen-3.8-27b", true)]
    [InlineData("account-custom-model", "account-custom-model", false)]
    public async Task ChatPreservesUnicodeModelAndProviderTemperature(string model, string expectedModel, bool reasoning)
    {
        using var plugin = Plugin(async (request, ct) =>
        {
            Assert.Equal("https://api.cerebras.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal(expectedModel, root.GetProperty("model").GetString());
            Assert.Equal("Übersetze.", root.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("Grüße 👋", root.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.False(root.TryGetProperty("max_tokens", out _));
            Assert.False(root.TryGetProperty("reasoning_format", out _));
            Assert.Equal(reasoning ? LlmOutputTokenBudget.CalculateWithReasoningReserve("Übersetze.", "Grüße 👋") : 2048,
                root.GetProperty("max_completion_tokens").GetInt32());
            return Json(Answer);
        });
        await Configure(plugin, new());
        Assert.Equal("Hello team.", await plugin.ProcessAsync("Übersetze.", "Grüße 👋", model, default));
    }

    [Fact]
    public async Task CustomTemperatureAndSelectedDefaultReachRequestAfterRestart()
    {
        var host = new TestPluginHostServices();
        using (var plugin = Plugin())
        {
            await Configure(plugin, host);
            await plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string>
            { ["selectedLlmModel"] = "qwen-3.8-27b", ["llmTemperatureMode"] = "custom", ["llmTemperatureValue"] = "0,7" }, null, default);
        }
        using var restarted = Plugin(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("qwen-3.8-27b", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
            return Json(Answer);
        });
        await restarted.ActivateAsync(host);
        await restarted.ProcessAsync("", "", "", default);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(408, PluginRequestFailureKind.Timeout)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(500, PluginRequestFailureKind.ServerError)]
    [InlineData(400, PluginRequestFailureKind.InvalidRequest)]
    public async Task HttpErrorsRetainClassificationAndRetryAfter(int status, PluginRequestFailureKind kind)
    {
        using var plugin = Plugin((_, _) =>
        {
            var response = Json("""{"error":{"message":"fixture error"}}""", (HttpStatusCode)status);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        await Configure(plugin, new());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "", "", default));
        Assert.Equal(kind, error.FailureKind);
        Assert.Equal(status, error.HttpStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), error.RetryAfter);
    }

    [Theory]
    [InlineData("", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("not json", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("[]", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{\"choices\":[42]}", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"reasoning\":\"hidden\",\"content\":null}}]}", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"partial\"}}]}", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"content\":\"partial\"}}]}", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}", PluginRequestFailureKind.OutputTruncated)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"content_filter\",\"message\":{\"content\":\"partial\"}}]}", PluginRequestFailureKind.OutputIncomplete)]
    public async Task InvalidOrIncompleteAnswersAreNotReturned(string body, PluginRequestFailureKind kind)
    {
        using var plugin = Plugin((_, _) => Task.FromResult(Json(body)));
        await Configure(plugin, new());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "", "", default));
        Assert.Equal(kind, error.FailureKind);
    }

    [Fact]
    public async Task CancellationReachesTransportAndPreCanceledRequestsStayOffline()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var offline = Plugin();
        await Configure(offline, new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offline.ProcessAsync("", "", "", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offline.ValidateConfigurationAsync(canceled.Token));
        using var cts = new CancellationTokenSource();
        using var online = Plugin(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Json(Answer); });
        await Configure(online, new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => online.ProcessAsync("", "", "", cts.Token));
    }

    [Fact]
    public async Task TimeoutAndNetworkFailuresStayClassified()
    {
        foreach (var timeout in new[] { false, true })
        {
            using var plugin = Plugin((_, _) => timeout ? throw new TaskCanceledException() : throw new HttpRequestException("offline"));
            await Configure(plugin, new());
            var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "", "", default));
            Assert.Equal(timeout ? PluginRequestFailureKind.Timeout : PluginRequestFailureKind.Network, error.FailureKind);
        }
    }

    [Fact]
    public async Task BillingFailureExplainsAccountActionAndIsNotRetried()
    {
        using var plugin = Plugin((_, _) => Task.FromResult(Json("""{"message":"Payment required"}""", HttpStatusCode.PaymentRequired)));
        await Configure(plugin, new());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "", "", default));
        Assert.Equal(402, error.HttpStatusCode);
        Assert.Equal(PluginRequestFailureKind.Permission, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.Contains("billing", error.Message);
    }

    [Fact]
    public async Task RefreshStagesSortedDeduplicatedCatalogUntilSave()
    {
        var host = new TestPluginHostServices();
        using var plugin = Plugin((request, _) =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("entered-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Json("""{"data":[{"id":"z-model"},{"id":"a-model","owned_by":"Cerebras"},{"id":"z-model"}]}"""));
        });
        await Configure(plugin, host);
        var writes = host.SettingWrites;
        var result = await plugin.ExecuteProfileActionAsync("cerebras", "refreshModels", Empty, "entered-key", default);
        Assert.True(result.HasPendingChanges);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal("gpt-oss-120b", plugin.SupportedModels[0].Id);
        Assert.Contains(plugin.TextSettings[1].Choices, choice => choice.Value == "a-model");
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("cerebras", Empty, "another-key", default));
        await plugin.SaveProfileSettingsAsync("cerebras", Empty, "entered-key", default);
        Assert.Equal(new[] { "a-model", "z-model" }, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal("a-model", Field(plugin, "selectedLlmModel"));
        Assert.Equal(writes + 1, host.SettingWrites);
        Assert.Equal("entered-key", Assert.Single(host.Secrets).Value);
        using var restarted = Plugin(); await restarted.ActivateAsync(host);
        Assert.Equal(new[] { "a-model", "z-model" }, restarted.SupportedModels.Select(m => m.Id));
    }

    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("not json")]
    [InlineData("{\"data\":[]}")] [InlineData("{\"data\":[{\"id\":42}]}")]
    [InlineData("{\"data\":[{\"id\":\"bad id\"}]}")]
    public async Task FailedRefreshPreservesCatalogAndSettings(string json)
    {
        var host = new TestPluginHostServices();
        using var plugin = Plugin((_, _) => Task.FromResult(Json(json))); await Configure(plugin, host);
        var writes = host.SettingWrites;
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(2, plugin.SupportedModels.Count);
    }

    [Fact]
    public async Task ConnectionCheckDoesNotSaveEnteredKeyOrCatalog()
    {
        using var plugin = Plugin((_, _) => Task.FromResult(Json("""{"data":[{"id":"discovered"}]}""")));
        var host = new TestPluginHostServices(); await plugin.ActivateAsync(host);
        var result = await plugin.ExecuteProfileActionAsync("cerebras", "checkConnection", Empty, "entered-key", default);
        Assert.False(result.HasPendingChanges); Assert.False(plugin.IsConfigured); Assert.Empty(host.Secrets);
        Assert.Equal(0, host.SettingWrites); Assert.Equal(2, plugin.SupportedModels.Count);
    }

    [Theory]
    [InlineData("llmTemperatureValue", "NaN")] [InlineData("llmTemperatureValue", "Infinity")]
    [InlineData("llmTemperatureValue", "-1")] [InlineData("llmTemperatureValue", "2.1")]
    [InlineData("llmTemperatureMode", "anything")] [InlineData("selectedLlmModel", "unknown")]
    [InlineData("unknown", "x")]
    public async Task InvalidFieldsDoNotSaveReplacementKeys(string id, string value)
    {
        var host = new TestPluginHostServices(); using var plugin = Plugin(); await Configure(plugin, host);
        var writes = host.SettingWrites;
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string> { [id] = value }, "new-key", default));
        Assert.Equal(writes, host.SettingWrites); Assert.Equal("fixture-key", Assert.Single(host.Secrets).Value);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FailedCommitPreservesAllActiveAndPersistedValues(bool failSecret)
    {
        var host = new TestPluginHostServices(); using var plugin = Plugin(); await Configure(plugin, host);
        host.FailWrites = failSecret; host.FailSettings = !failSecret;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string> { ["llmTemperatureMode"] = "custom" }, "new-key", default));
        Assert.True(plugin.IsAvailable); Assert.Equal("providerDefault", Field(plugin, "llmTemperatureMode"));
        Assert.Equal("fixture-key", Assert.Single(host.Secrets).Value);
        host.FailWrites = host.FailSettings = false;
        using var restarted = Plugin(); await restarted.ActivateAsync(host);
        Assert.True(restarted.IsAvailable); Assert.Equal("providerDefault", Field(restarted, "llmTemperatureMode"));
    }

    [Fact]
    public async Task RemoveKeyAndDeactivateClearAvailabilityWithoutLegacyMigration()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "legacy-fixture";
        using var plugin = Plugin(); await plugin.ActivateAsync(host); Assert.False(plugin.IsAvailable);
        await plugin.SetApiKeyAsync(" new-key "); Assert.True(plugin.IsAvailable);
        await plugin.SetApiKeyAsync(""); Assert.False(plugin.IsAvailable);
        Assert.Equal("legacy-fixture", Assert.Single(host.Secrets).Value);
        await plugin.SetApiKeyAsync("another-key"); await plugin.DeactivateAsync(); Assert.False(plugin.IsAvailable);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "", "", default));
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
    }

    private static string Field(CerebrasPlugin plugin, string id) => plugin.TextSettings.Single(f => f.Id == id).Value;
}
