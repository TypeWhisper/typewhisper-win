using TypeWhisper.Plugin.Fireworks;

public sealed partial class ProviderTests
{
    [Fact]
    public async Task FreshConfigurationUsesLiveValidatedDefaultsAndPreservesSavedSelections()
    {
        var host = new Host();
        using var plugin = new FireworksPlugin();
        await plugin.ActivateAsync(host);
        Assert.Equal("whisper-v3-turbo", plugin.SelectedModelId);
        Assert.Equal(plugin.SelectedModelId, plugin.TextSettings.Single(f => f.Id == "model").Value);
        Assert.Equal("accounts/fireworks/models/gpt-oss-120b", plugin.TextSettings.Single(f => f.Id == "llmModel").Value);
        plugin.SelectModel("whisper-v3");
        await plugin.SaveTextSettingAsync("llmModel", "custom-deployment", default);
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Equal("whisper-v3", plugin.SelectedModelId);
        Assert.Equal("whisper-v3", plugin.TextSettings.Single(f => f.Id == "model").Value);
        Assert.Equal("custom-deployment", plugin.TextSettings.Single(f => f.Id == "llmModel").Value);
    }

    [Fact]
    public async Task RefreshedCatalogDoesNotReintroduceRetiredFallbackModels()
    {
        using var http = new HttpClient(new Handler((_, _) => Json("""{"data":[{"id":"accounts/fireworks/models/current-chat"}]}""")));
        using var plugin = new FireworksPlugin(http);
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        await plugin.SaveTextSettingAsync("llmModel", "my-deployment", default);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.Equal(new[] { "my-deployment", "accounts/fireworks/models/current-chat" }, plugin.SupportedModels.Select(m => m.Id));
    }
}
