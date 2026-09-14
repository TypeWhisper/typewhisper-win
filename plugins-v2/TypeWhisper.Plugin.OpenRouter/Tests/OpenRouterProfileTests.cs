using System.Net;
using TypeWhisper.Plugin.OpenRouter;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenRouterPluginTests
{
    [Fact]
    public async Task OneSaveCommitsAllFieldsAndReplacementKeyAcrossRestart()
    {
        var host = new TestPluginHostServices(); using var plugin = new OpenRouterPlugin();
        await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("previous");
        var before = host.SettingWrites;
        await plugin.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string>
        {
            ["selectedTranscriptionModel"] = "openai/whisper-1",
            ["selectedLlmModel"] = "openai/gpt-4o",
            ["llmTemperatureMode"] = "custom", ["llmTemperatureValue"] = "1.2"
        }, "replacement", default);
        Assert.Equal(before + 1, host.SettingWrites);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("replacement", plugin.ApiKey);
        Assert.Equal("openai/whisper-1", plugin.SelectedModelId);
        Assert.Equal("openai/gpt-4o", plugin.SelectedLlmModelId);
        Assert.Equal("custom", plugin.TemperatureMode); Assert.Equal(1.2, plugin.TemperatureValue);
        Assert.Single(host.Secrets);
        // Host-driven model selection must update the same persisted configuration.
        plugin.SelectModel("openai/gpt-4o-mini-transcribe");
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("openai/gpt-4o-mini-transcribe", plugin.SelectedModelId);
    }

    [Fact]
    public async Task FailedCommitDoesNotActivateStagedKeyOrPartialFields()
    {
        var host = new TestPluginHostServices(); using var plugin = new OpenRouterPlugin();
        await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("previous");
        host.FailSettings = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string>
        { ["llmTemperatureMode"] = "custom", ["llmTemperatureValue"] = "1.5" }, "replacement", default));
        Assert.Equal("previous", plugin.ApiKey); Assert.Equal("providerDefault", plugin.TemperatureMode);
        Assert.Equal(0.3, plugin.TemperatureValue); Assert.Single(host.Secrets);
        host.FailSettings = false;
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("previous", plugin.ApiKey); Assert.Equal(0.3, plugin.TemperatureValue);
    }

    [Fact]
    public async Task InvalidBatchRejectsEverythingBeforeWriting()
    {
        var host = new TestPluginHostServices(); using var plugin = new OpenRouterPlugin();
        await plugin.ActivateAsync(host); var before = host.SettingWrites;
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string>
        { ["llmTemperatureMode"] = "custom", ["llmTemperatureValue"] = "NaN" }, "replacement", default));
        Assert.Equal(before, host.SettingWrites); Assert.Empty(host.Secrets);
        Assert.Equal("providerDefault", plugin.TemperatureMode);
    }

    [Fact]
    public async Task DraftConnectionAndCatalogUseUnsavedKeyAndWaitForSave()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal("draft-key", request.Headers.Authorization!.Parameter);
            return JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/key") ? """{"data":{"limit_remaining":1}}"""
                : request.RequestUri.Query.Length == 0
                ? """{"data":[{"id":"provider/text","name":"Text","architecture":{"modality":"text->text"}}]}"""
                : """{"data":[{"id":"provider/speech","name":"Speech"}]}""");
        })));
        var host = new TestPluginHostServices(); await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("saved-key");
        var writes = host.SettingWrites;
        await plugin.ExecuteProfileActionAsync("openrouter", "checkConnection", new Dictionary<string, string>(), "draft-key", default);
        var refreshed = await plugin.ExecuteProfileActionAsync("openrouter", "refreshModels", new Dictionary<string, string>(), "draft-key", default);
        Assert.True(refreshed.HasPendingChanges); Assert.Equal(writes, host.SettingWrites);
        Assert.Equal("saved-key", plugin.ApiKey);
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "provider/text");
        Assert.Contains(plugin.TextSettings.Single(f => f.Id == "selectedLlmModel").Choices, c => c.Value == "provider/text");
        await plugin.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string>
        { ["selectedLlmModel"] = "provider/text", ["selectedTranscriptionModel"] = "provider/speech" }, null, default);
        Assert.Equal("saved-key", plugin.ApiKey); Assert.Equal("provider/text", plugin.SelectedLlmModelId);
        Assert.Equal("provider/speech", plugin.SelectedModelId);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "provider/text");
    }

    [Fact]
    public async Task AdvertisedActionsAndDraftModelChoicesWorkThroughGenericInterfaces()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((request, _) =>
            JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/key") ? """{"data":{"limit_remaining":1}}"""
                : request.RequestUri.Query.Length == 0
                ? """{"data":[{"id":"provider/text","name":"Text","architecture":{"modality":"text->text"}}]}"""
                : """{"data":[{"id":"provider/speech","name":"Speech"}]}"""))));
        var host = new TestPluginHostServices(); await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("saved-key");
        var writes = host.SettingWrites;
        foreach (var action in plugin.SettingsActions)
            Assert.False(string.IsNullOrWhiteSpace(await plugin.ExecuteSettingsActionAsync(action.Id, default)));
        Assert.Equal(writes, host.SettingWrites);
        await plugin.SaveTextSettingAsync("configurationProfile", "openrouter", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("selectedLlmModel", "unknown", default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("selectedTranscriptionModel", "unknown", default));
        await plugin.SaveTextSettingAsync("selectedLlmModel", "provider/text", default);
        await plugin.SaveTextSettingAsync("selectedTranscriptionModel", "provider/speech", default);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("provider/text", plugin.SelectedLlmModelId);
        Assert.Equal("provider/speech", plugin.SelectedModelId);
    }

    [Fact]
    public async Task ActivationRepairsAndPersistsInvalidSelectionsOnlyOnce()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("configuration", new
        {
            SpeechModel = "removed/speech", TextModel = "removed/text", UserSelectedTextModel = true,
            TemperatureMode = "custom", Temperature = 0.7,
            SpeechModels = new[] { new OpenRouterFetchedModel("provider/speech", "Speech", "0", "0") },
            TextModels = new[] { new OpenRouterFetchedModel("provider/text", "Text", "0", "0") },
            SecretName = "saved-key"
        });
        await host.StoreSecretAsync("saved-key", "fixture-key");
        var writes = host.SettingWrites;
        using var plugin = new OpenRouterPlugin(); await plugin.ActivateAsync(host);
        Assert.Equal("provider/speech", plugin.SelectedModelId);
        Assert.Equal(OpenRouterPlugin.DefaultLlmModelId, plugin.SelectedLlmModelId);
        Assert.Equal("fixture-key", plugin.ApiKey); Assert.Equal(0.7, plugin.TemperatureValue);
        Assert.Equal(writes + 1, host.SettingWrites);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(writes + 1, host.SettingWrites);
        Assert.Equal("provider/speech", plugin.SelectedModelId);
        Assert.Equal(OpenRouterPlugin.DefaultLlmModelId, plugin.SelectedLlmModelId);
    }

    [Fact]
    public async Task EmptyReplacementFieldKeepsKeyUntilExplicitRemoval()
    {
        var host = new TestPluginHostServices(); using var plugin = new OpenRouterPlugin();
        await plugin.ActivateAsync(host); await plugin.SetApiKeyAsync("saved-key");
        await plugin.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string>(), "", default);
        Assert.Equal("saved-key", plugin.ApiKey); Assert.Single(host.Secrets);
        await plugin.SetApiKeyAsync("");
        Assert.False(plugin.IsConfigured); Assert.Empty(host.Secrets);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.False(plugin.IsConfigured);
    }

    [Fact]
    public async Task RefreshRepairsRemovedSelectionsAtTheAtomicSavePoint()
    {
        using var plugin = new OpenRouterPlugin(new HttpClient(new CapturingHandler((request, _) =>
            JsonResponse(request.RequestUri!.Query.Length == 0
                ? """{"data":[{"id":"provider/new-text","name":"New text","architecture":{"modality":"text->text"}}]}"""
                : """{"data":[{"id":"provider/new-speech","name":"New speech"}]}"""))));
        var host = new TestPluginHostServices(); await plugin.ActivateAsync(host);
        plugin.SetFetchedModels([new("provider/old-text", "Old text", "0", "0")]);
        plugin.SetFetchedTranscriptionModels([new("provider/old-speech", "Old speech", "0", "0")]);
        plugin.SelectLlmModel("provider/old-text"); plugin.SelectModel("provider/old-speech");
        await plugin.ExecuteProfileActionAsync("openrouter", "refreshModels", new Dictionary<string, string>(), null, default);
        var values = new Dictionary<string, string>
        { ["selectedLlmModel"] = "provider/old-text", ["selectedTranscriptionModel"] = "provider/old-speech" };
        host.FailSettings = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync("openrouter", values, null, default));
        Assert.Equal("provider/old-text", plugin.SelectedLlmModelId);
        Assert.Equal("provider/old-speech", plugin.SelectedModelId);
        host.FailSettings = false; var writes = host.SettingWrites;
        await plugin.SaveProfileSettingsAsync("openrouter", values, null, default);
        Assert.Equal(writes + 1, host.SettingWrites);
        Assert.Equal(OpenRouterPlugin.DefaultLlmModelId, plugin.SelectedLlmModelId);
        Assert.Equal("provider/new-speech", plugin.SelectedModelId);
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "provider/old-text");
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(writes + 1, host.SettingWrites);
        Assert.Equal("provider/new-speech", plugin.SelectedModelId);
    }
}
