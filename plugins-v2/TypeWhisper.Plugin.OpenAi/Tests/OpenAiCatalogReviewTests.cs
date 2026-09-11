using System.Net;
using TypeWhisper.Plugin.OpenAi;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
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
