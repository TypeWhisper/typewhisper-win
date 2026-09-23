using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.OpenRouter;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenRouterPluginTests
{
    [Fact]
    public async Task LanguageMetadataFollowsModelSelectionAndRestart()
    {
        var host = new TestPluginHostServices(); using var plugin = new OpenRouterPlugin();
        await plugin.ActivateAsync(host);
        Assert.Contains("en", plugin.SupportedLanguages); Assert.Contains("de", plugin.SupportedLanguages);
        Assert.Contains("de", plugin.TranscriptionModels.Single(m => m.Id == plugin.SelectedModelId).LanguageCodes);
        plugin.SelectModel("google/chirp-3"); Assert.Empty(plugin.SupportedLanguages);
        plugin.SelectModel("openai/gpt-4o-mini-transcribe");
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Contains("de", plugin.SupportedLanguages);
    }

    [Fact]
    public async Task PortableSettingsDoNotFetchOrExposeSecretsAndPersistAcrossRestart()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((_, _) => throw new Exception("Unexpected request"))));
        var host = new TestPluginHostServices();
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("  private-fixture-key  ");
        Assert.DoesNotContain("private-fixture-key", JsonSerializer.Serialize(plugin.TextSettings));
        Assert.Equal(3, plugin.SettingsActions.Count);
        await plugin.SaveTextSettingAsync("llmTemperatureMode", "custom", default);
        await plugin.SaveTextSettingAsync("llmTemperatureValue", "1,2", default);
        await plugin.SaveTextSettingAsync("selectedLlmModel", "openai/gpt-4o", default);
        await plugin.DeactivateAsync();
        Assert.False(plugin.IsConfigured);
        await plugin.ActivateAsync(host);
        Assert.Equal("openai/gpt-4o", plugin.SelectedLlmModelId);
        Assert.Equal(1.2, plugin.TemperatureValue);
        Assert.Equal("custom", plugin.TemperatureMode);
        await plugin.SetApiKeyAsync("");
        Assert.False(plugin.IsConfigured);
        Assert.Empty(host.Secrets);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("2.1")]
    [InlineData("")]
    public async Task InvalidTemperatureDoesNotChangeSettings(string value)
    {
        using var plugin = new OpenRouterPlugin(); await plugin.ActivateAsync(new TestPluginHostServices());
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("llmTemperatureValue", value, default));
        Assert.Equal(0.3, plugin.TemperatureValue);
    }

    [Fact]
    public async Task FailedWritesPreserveKeyAndTemperature()
    {
        var host = new TestPluginHostServices(); using var plugin = new OpenRouterPlugin();
        await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("old");
        host.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync("new"));
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync(""));
        Assert.Equal("old", plugin.ApiKey);
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync("llmTemperatureValue", "1.5", default));
        Assert.Equal(0.3, plugin.TemperatureValue);
    }

    [Fact]
    public async Task RefreshFailurePreservesCatalogAndSelection()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((_, _) => new(HttpStatusCode.ServiceUnavailable))));
        await plugin.ActivateAsync(new TestPluginHostServices());
        plugin.SetFetchedModels([new("a/test", "Test", "0", "0")]); plugin.SelectLlmModel("a/test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ExecuteSettingsActionAsync("refreshTextModels", default));
        Assert.Equal("a/test", plugin.SelectedLlmModelId);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "a/test");
    }

    [Fact]
    public async Task KeyValidationAndBudgetUseCurrentEndpointAndReportedRemainingLimit()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/key", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return JsonResponse("""{"data":{"limit":10,"usage":100,"limit_remaining":7.5}}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        await plugin.ValidateConfigurationAsync(default);
        Assert.Contains("7.50", await plugin.ExecuteSettingsActionAsync("checkBudget", default));
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(402, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public async Task RequestErrorsKeepClassification(int status, PluginRequestFailureKind kind)
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((_, _) => new((HttpStatusCode)status)
            { Content = new StringContent("""{"error":{"message":"fixture"}}""") })));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("s", "u", "", default));
        Assert.Equal(kind, error.FailureKind);
    }

    [Theory]
    [InlineData("<html>oops</html>", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("{\"error\":{\"code\":429}}", PluginRequestFailureKind.RateLimit)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}", PluginRequestFailureKind.OutputTruncated)]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null,\"reasoning\":\"private\"}}]}", PluginRequestFailureKind.EmptyResponse)]
    public async Task InvalidAndIncompleteChatResponsesFail(string json, PluginRequestFailureKind kind)
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((_, _) => JsonResponse(json))));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        Assert.Equal(kind, (await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("s", "u", "", default))).FailureKind);
    }

    [Fact]
    public async Task TypedChatPartsReturnOnlyVisibleText()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((httpRequest, body) =>
        {
            Assert.Contains("\\u00", body);
            return JsonResponse("""{"choices":[{"message":{"content":[{"type":"text","text":"Grüße "},{"type":"reasoning","text":"private"},{"type":"text","text":"zurück"}]}}]}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        Assert.Equal("Grüße zurück", await plugin.ProcessAsync("s", "Grüße", "", default));
    }

    [Theory]
    [InlineData("openai/whisper-1", true)]
    [InlineData("google/chirp-3", false)]
    public async Task TranscriptionRequestsAndPreservesSupportedTimestamps(string model, bool timed)
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((httpRequest, body) =>
        {
            using var request = JsonDocument.Parse(body!);
            Assert.Equal(timed, request.RootElement.TryGetProperty("response_format", out _));
            Assert.False(request.RootElement.TryGetProperty("language", out _));
            return JsonResponse("""{"text":" Hello ","language":"en","usage":{"seconds":1.25},"segments":[{"text":"Hello","start":0,"end":1.25},{"text":"bad","start":3,"end":1}]}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture"); plugin.SelectModel(model);
        var result = await plugin.TranscribeAsync([0, 1], "auto", false, null, default);
        Assert.Equal("Hello", result.Text); Assert.Equal(1.25, result.DurationSeconds); Assert.Single(result.Segments);
    }

    [Fact]
    public async Task CancellationPreventsRequestsAndSaves()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((_, _) => throw new Exception("Unexpected request"))));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ProcessAsync("s", "u", "", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExecuteSettingsActionAsync("refreshTextModels", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.SaveTextSettingAsync("llmTemperatureValue", "1", cts.Token));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("transcription")]
    [InlineData("catalog")]
    [InlineData("validation")]
    public async Task InFlightRequestsHonorCancellation(string operation)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = new OpenRouterPlugin(new HttpClient(new PendingHandler(started)));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        using var cts = new CancellationTokenSource();
        Task task = operation switch
        {
            "chat" => plugin.ProcessAsync("s", "u", "", cts.Token),
            "transcription" => plugin.TranscribeAsync([0, 1], null, false, null, cts.Token),
            "catalog" => plugin.ExecuteSettingsActionAsync("refreshTextModels", cts.Token),
            _ => plugin.ValidateConfigurationAsync(cts.Token)
        };
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("<html>upstream failure</html>")]
    public async Task MalformedTranscriptDoesNotBecomeSuccessfulEmptyText(string response)
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((_, _) => JsonResponse(response))));
        await plugin.ActivateAsync(new TestPluginHostServices()); await plugin.SetApiKeyAsync("fixture");
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete,
            (await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync([0, 1], null, false, null, default))).FailureKind);
    }

    private sealed class PendingHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
