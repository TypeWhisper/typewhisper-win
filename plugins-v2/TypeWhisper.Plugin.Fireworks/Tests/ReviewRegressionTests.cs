using TypeWhisper.Plugin.Fireworks;
using TypeWhisper.PluginSDK;

public sealed partial class ProviderTests
{
    [Theory]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"models\":\"unavailable\"}")]
    public async Task ValidationRejectsMalformedCatalogProperties(string json)
    {
        using var plugin = new FireworksPlugin(new HttpClient(new Handler((_, _) => Json(json))));
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ValidateConfigurationAsync(default));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Fact]
    public async Task ModelIdLimitMatchesRequestValidationAndPreservesPriorSetting()
    {
        using var plugin = new FireworksPlugin();
        await plugin.ActivateAsync(new Host());
        var valid = new string('a', 256);
        await plugin.SaveTextSettingAsync("llmModel", valid, default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("llmModel", valid + "b", default));
        Assert.Equal(valid, plugin.SupportedModels[0].Id);
        Assert.True(plugin.SupportedModels[0].IsRecommended);
    }

    [Fact]
    public async Task CatalogExcludesRerankersAndExplicitNonChatModels()
    {
        using var plugin = new FireworksPlugin(new HttpClient(new Handler((_, _) => Json("""
            {"data":[{"id":"chat-model","supports_chat":true},{"id":"special-ranker","kind":"EMBEDDING_MODEL","supports_chat":true},{"id":"non-chat","supports_chat":false}]}
            """))));
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "chat-model");
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id is "special-ranker" or "non-chat");
    }
    [Theory]
    [InlineData("accounts/fireworks/models/gpt-oss-120b", 27048)]
    [InlineData("accounts/fireworks/models/gpt-oss-20b", 27048)]
    [InlineData("accounts/fireworks/models/llama-v3p1-8b-instruct", 2048)]
    [InlineData("custom-deployment", 2048)]
    public async Task ChatUsesReasoningReserveOnlyForKnownReasoningModels(string model, int expected)
    {
        using var plugin = new FireworksPlugin(new HttpClient(new Handler((_, body) =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(body!);
            Assert.Equal(expected, json.RootElement.GetProperty("max_tokens").GetInt32());
            return Json("""{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""");
        })));
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        Assert.Equal("done", await plugin.ProcessAsync("Fix", "Text", model, default));
    }

    [Fact]
    public async Task TranscriptionRequestsVerboseJsonAndKeepsSubtitleSegments()
    {
        using var plugin = new FireworksPlugin(new HttpClient(new Handler((_, body) =>
        {
            Assert.Contains("verbose_json", body);
            return Json("""{"text":"Hello","language":"en","duration":1.25,"segments":[{"text":"Hello","start":0,"end":1.25,"no_speech_prob":0.01}]}""");
        })));
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        var result = await plugin.TranscribeAsync(Audio(), "en", false, null, default);
        Assert.Single(result.Segments);
        Assert.Equal("Hello", result.Text);
    }
}
