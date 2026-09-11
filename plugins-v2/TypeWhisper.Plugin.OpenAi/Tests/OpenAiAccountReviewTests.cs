using TypeWhisper.Plugin.OpenAi;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Fact]
    public async Task FailedTranscriptionSelectionKeepsThePreviousModel()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiPlugin();
        await plugin.ActivateAsync(host);
        plugin.SelectModel("whisper-1");
        host.FailSettingKey = "selectedModel";
        var other = plugin.TranscriptionModels.First(model => model.Id != "whisper-1").Id;
        Assert.Throws<IOException>(() => plugin.SelectModel(other));
        Assert.Equal("whisper-1", plugin.SelectedModelId);
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Equal("whisper-1", reloaded.SelectedModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatGptModeRefreshesApiTranscriptionModelsIndependently(bool apiUnavailable)
    {
        var urls = new List<string>();
        using var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            urls.Add(request.RequestUri!.Host);
            if (apiUnavailable && request.RequestUri.Host == "api.openai.com")
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(JsonResponse(request.RequestUri.Host == "api.openai.com"
                ? """{"data":[{"id":"whisper-1"}]}"""
                : """{"models":[{"slug":"gpt-5.5","visibility":"list"}]}"""));
        }));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "api-key";
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        var result = await plugin.ExecuteSettingsActionAsync("refresh", default);
        if (apiUnavailable) Assert.Contains("Some models could not be refreshed", result);
        else Assert.Equal("Model list refreshed.", result);
        Assert.Contains("api.openai.com", urls);
        Assert.Contains("chatgpt.com", urls);
        if (!apiUnavailable) Assert.Equal("whisper-1", Assert.Single(plugin.TranscriptionModels).Id);
        Assert.Equal("gpt-5.5", Assert.Single(plugin.SupportedModels).Id);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("secret")]
    [InlineData("after-secret")]
    public async Task AccountChangeFailureNeverPairsNewTokensWithTheOldCatalog(string failure)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, """{"tokens":{"access_token":"new-access","refresh_token":"new-refresh","account_id":"new-account"}}""");
            var host = new TestPluginHostServices();
            const string original = """{"AccessToken":"old-access","RefreshToken":"old-refresh","AccountId":"old-account"}""";
            host.Secrets["chatgpt-session"] = original;
            host.SetSetting("authMode", "chatgpt");
            host.SetSetting("chatGPTModelCatalogSnapshot", new OpenAiPlugin.ChatGptCatalogSnapshot(true,
                [new("old-model", "Old model", "list", null, null)]));
            using var plugin = new OpenAiPlugin();
            await plugin.ActivateAsync(host);
            host.FailSettingKey = failure == "snapshot" ? "chatGPTModelCatalogSnapshot" : null;
            host.FailSecretWrites = failure == "secret";
            host.FailAfterSecretWrite = failure == "after-secret";
            await Assert.ThrowsAsync<IOException>(() => plugin.ImportExistingLoginAsync(file));
            using var reloaded = new OpenAiPlugin();
            await reloaded.ActivateAsync(host);
            if (failure == "after-secret")
            {
                Assert.Contains("new-account", host.Secrets["chatgpt-session"]);
                Assert.DoesNotContain(reloaded.SupportedModels, model => model.Id == "old-model");
            }
            else
            {
                Assert.Equal(original, host.Secrets["chatgpt-session"]);
                Assert.Equal("old-model", Assert.Single(reloaded.SupportedModels).Id);
            }
        }
        finally { File.Delete(file); }
    }
}
