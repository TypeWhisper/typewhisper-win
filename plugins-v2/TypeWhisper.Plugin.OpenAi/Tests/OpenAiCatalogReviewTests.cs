using System.Net;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Theory]
    [InlineData("replacement-key")]
    [InlineData("")]
    public async Task FailedApiCatalogInvalidationKeepsTheExistingKeyAndModels(string replacement)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(
            JsonResponse("""{"data":[{"id":"whisper-1"},{"id":"o3"}]}"""))));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "existing-key";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        await plugin.RefreshAvailableLlmModelsAsync();
        host.FailSettingKey = "apiModelCatalogSnapshot";
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync(replacement));
        Assert.Equal("existing-key", host.Secrets["api-key"]);
        Assert.Equal("existing-key", plugin.ApiKey);
        Assert.Equal("o3", Assert.Single(plugin.SupportedModels).Id);
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Equal("existing-key", reloaded.ApiKey);
        Assert.Equal("o3", Assert.Single(reloaded.SupportedModels).Id);
        Assert.Equal("whisper-1", Assert.Single(reloaded.TranscriptionModels).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedApiSnapshotWriteKeepsBothPreviousCatalogs(bool previouslyFetched)
    {
        var next = false;
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse(next
            ? """{"data":[{"id":"gpt-4.1-mini"}]}"""
            : """{"data":[{"id":"whisper-1"},{"id":"o3"}]}"""))));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        if (previouslyFetched) await plugin.RefreshAvailableLlmModelsAsync();
        var text = plugin.SupportedModels.Select(model => model.Id).ToArray();
        var audio = plugin.TranscriptionModels.Select(model => model.Id).ToArray();
        next = true;
        host.FailSettingKey = "apiModelCatalogSnapshot";
        await Assert.ThrowsAsync<IOException>(() => plugin.RefreshAvailableLlmModelsAsync());
        Assert.Equal(text, plugin.SupportedModels.Select(model => model.Id));
        Assert.Equal(audio, plugin.TranscriptionModels.Select(model => model.Id));
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Equal(text, reloaded.SupportedModels.Select(model => model.Id));
        Assert.Equal(audio, reloaded.TranscriptionModels.Select(model => model.Id));
    }

    [Fact]
    public async Task FailedChatGptSnapshotWriteDoesNotPersistFetchedMarker()
    {
        using var client = new HttpClient(new CapturingHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"models":[]}"""))));
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        host.FailSettingKey = "fetchedChatGPTModels";
        await Assert.ThrowsAsync<IOException>(() => plugin.RefreshAvailableLlmModelsAsync());
        Assert.False(host.GetSetting<bool>("hasFetchedChatGPTModelCatalog"));
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.NotEmpty(reloaded.SupportedModels);
    }

    [Theory]
    [InlineData("""{"models":[{"slug":"gpt-5.5","visibility":"list","available_in_plans":["pro"]}]}""")]
    [InlineData("""{"models":[{"slug":"gpt-5.5","visibility":"hide"}]}""")]
    [InlineData("""{"models":[]}""")]
    public async Task EmptyChatGptCatalogReplacesCachedModelsAndSurvivesReload(string catalog)
    {
        var fail = false;
        using var client = new HttpClient(new CapturingHandler((_, _) =>
            Task.FromResult(JsonResponse(fail ? "invalid" : catalog))));
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthPlanType", "plus");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        Assert.NotEmpty(plugin.SupportedModels);
        await plugin.RefreshAvailableLlmModelsAsync();
        Assert.Empty(plugin.SupportedModels);
        Assert.Null(plugin.SelectedLlmModelId);
        Assert.True(host.GetSetting<bool>("hasFetchedChatGPTModelCatalog"));
        fail = true;
        await plugin.RefreshAvailableLlmModelsAsync();
        Assert.Empty(plugin.SupportedModels);
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Empty(reloaded.SupportedModels);
        Assert.Null(reloaded.SelectedLlmModelId);
        await reloaded.ClearChatGptLoginAsync();
        Assert.False(host.GetSetting<bool>("hasFetchedChatGPTModelCatalog"));
    }

    [Fact]
    public async Task EmptyDiscoveredTranscriptionCatalogSurvivesReloadAndLaterRecovers()
    {
        var audio = false;
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse(audio
            ? """{"data":[{"id":"whisper-1"}]}"""
            : """{"data":[{"id":"gpt-4.1-mini"}]}"""))));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        await plugin.RefreshAvailableLlmModelsAsync();
        Assert.Empty(plugin.TranscriptionModels);
        Assert.False(((ITranscriptionEnginePlugin)plugin).IsConfigured);
        Assert.True(((IApiKeyPlugin)plugin).IsConfigured);
        Assert.True(((ITtsProviderPlugin)plugin).IsConfigured);
        Assert.Null(plugin.SelectedModelId);
        plugin.SelectModel("whisper-1");
        Assert.Null(plugin.SelectedModelId);
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Empty(reloaded.TranscriptionModels);
        Assert.Null(reloaded.SelectedModelId);

        audio = true;
        await plugin.RefreshAvailableLlmModelsAsync();
        Assert.Equal("whisper-1", Assert.Single(plugin.TranscriptionModels).Id);
        Assert.Equal("whisper-1", plugin.SelectedModelId);
        Assert.True(((ITranscriptionEnginePlugin)plugin).IsConfigured);
        await reloaded.SetApiKeyAsync("replacement-key");
        Assert.NotEmpty(reloaded.TranscriptionModels);
        Assert.NotNull(reloaded.SelectedModelId);
    }

    [Fact]
    public async Task EmptyDiscoveredTextCatalogSurvivesReloadFailureAndKeyReplacement()
    {
        var fail = false;
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : JsonResponse("""{"data":[{"id":"whisper-1"}]}"""))));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        Assert.NotEmpty(plugin.SupportedModels);

        Assert.Equal("Model list refreshed.", await plugin.ExecuteSettingsActionAsync("refresh", default));
        Assert.Empty(plugin.SupportedModels);
        Assert.Null(plugin.SelectedLlmModelId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ProcessAsync("", "Hello", "", default));

        fail = true;
        Assert.Contains("could not be refreshed", await plugin.ExecuteSettingsActionAsync("refresh", default));
        Assert.Empty(plugin.SupportedModels);
        using var reloaded = new OpenAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Empty(reloaded.SupportedModels);
        Assert.Null(reloaded.SelectedLlmModelId);
        await reloaded.SetApiKeyAsync("replacement-key");
        Assert.NotEmpty(reloaded.SupportedModels);
        using var afterKeyChange = new OpenAiPlugin();
        await afterKeyChange.ActivateAsync(host);
        Assert.NotEmpty(afterKeyChange.SupportedModels);
    }

    [Fact]
    public async Task FineTunedChatModelsRetainTheirFullIdInDiscovery()
    {
        const string model = "ft:gpt-4o-mini-2024-07-18:org:audio-project:fixture";
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(
            JsonResponse("{\"data\":[{\"id\":\"" + model + "\"}]}"))));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        var models = await plugin.RefreshAvailableLlmModelsAsync();
        Assert.Equal(model, Assert.Single(models).Id);
        Assert.Equal(model, plugin.SelectedLlmModelId);
    }

    [Theory]
    [InlineData("ft:whisper-1:org:fixture")]
    [InlineData("ft:gpt-4o-mini-transcribe:org:fixture")]
    [InlineData("ft:gpt-image-1:org:fixture")]
    [InlineData("ft:gpt-4o-mini")]
    public void FineTunedNonChatOrIncompleteModelsAreExcluded(string model) => Assert.False(OpenAiPlugin.IsChatModel(model));
}
