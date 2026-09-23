using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public partial class OpenAiCompatiblePluginTests
{
    private const string Default = OpenAiCompatiblePlugin.DefaultProfileId;

    [Theory]
    [InlineData("check", false)]
    [InlineData("refresh", true)]
    public async Task DraftActionsUseEnteredConnectionWithoutSavingFieldsOrKey(string action, bool pending)
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal("http://localhost:1234/v1/models?api-version=preview", request.RequestUri!.ToString());
            Assert.Equal("draft-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""{"data":[{"id":"qwen/qwen3-4b"}]}""");
        })));
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("saved-key");
        var saved = host.GetRawSettingJson("profiles");
        var result = await plugin.ExecuteProfileActionAsync(Default, Default + "/" + action,
            new Dictionary<string, string> { [Default + "/url"] = "http://localhost:1234/v1", [Default + "/api-version"] = "preview" }, "draft-key", default);
        Assert.Equal(pending, result.HasPendingChanges);
        Assert.Equal(saved, host.GetRawSettingJson("profiles"));
        Assert.Equal("", plugin.Profiles[0].BaseUrl);
        Assert.Equal("saved-key", plugin.GetApiKey());
        Assert.Empty(plugin.Profiles[0].FetchedModels);
        Assert.Equal(pending, plugin.TextSettings.Single(f => f.Id == Default + "/text").Suggestions.Contains("qwen/qwen3-4b"));
    }

    [Theory]
    [InlineData("http://localhost:1234", "preview", true)]
    [InlineData("http://localhost:5678", "preview", false)]
    [InlineData("http://localhost:1234", "other-version", false)]
    public async Task DraftCatalogIsCommittedOnlyWithTheMatchingConnection(string savedUrl, string savedVersion, bool includesCatalog)
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((_, _) =>
            JsonResponse("""{"data":[{"id":"local-text"}]}"""))));
        await plugin.ActivateAsync(host);
        await plugin.ExecuteProfileActionAsync(Default, Default + "/refresh", new Dictionary<string, string>
        { [Default + "/url"] = "http://localhost:1234", [Default + "/api-version"] = "preview" }, null, default);
        host.FailProfileWrites = true;
        var values = new Dictionary<string, string> { [Default + "/url"] = savedUrl, [Default + "/api-version"] = savedVersion };
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync(Default, values, null, default));
        Assert.Contains("local-text", plugin.TextSettings.Single(f => f.Id == Default + "/text").Suggestions);
        host.FailProfileWrites = false;
        await plugin.SaveProfileSettingsAsync(Default, values, null, default);
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        Assert.Equal(includesCatalog, restarted.Profiles[0].FetchedModels.Any(m => m.Id == "local-text"));
    }

    [Fact]
    public async Task DraftActionsRejectStaleProfilesBeforeMakingRequests()
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((_, _) => throw new InvalidOperationException("Unexpected request"))));
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.ExecuteSettingsActionAsync("add", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.ExecuteProfileActionAsync(Default, Default + "/check",
            new Dictionary<string, string>(), null, default));
    }

    [Fact]
    public async Task OneSaveCommitsTheProfileAndEncryptedKeyAcrossRestart()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("previous-key");
        await plugin.SaveProfileSettingsAsync(Default, new Dictionary<string, string>
        {
            [Default + "/name"] = "Saved server",
            [Default + "/url"] = "http://localhost:1234"
        }, "replacement-key", default);
        Assert.Equal("replacement-key", plugin.GetApiKey());
        Assert.Single(host.Secrets);
        Assert.DoesNotContain("replacement-key", host.GetRawSettingJson("profiles"));
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        Assert.Equal("replacement-key", restarted.GetApiKey());
        Assert.Equal("Saved server", restarted.Profiles[0].Name);
        await restarted.SaveProfileSettingsAsync(Default, new Dictionary<string, string> { [Default + "/name"] = "Renamed" }, null, default);
        Assert.Equal("replacement-key", restarted.GetApiKey());
    }

    [Fact]
    public async Task FailedProfileCommitKeepsThePreviousKeyAndFieldsAcrossRestart()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("previous-key");
        host.FailProfileWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync(Default,
            new Dictionary<string, string> { [Default + "/name"] = "Not saved" }, "new-key", default));
        Assert.Equal("previous-key", plugin.GetApiKey());
        Assert.Equal("OpenAI Compatible", plugin.Profiles[0].Name);
        Assert.Single(host.Secrets);
        host.FailProfileWrites = false;
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        Assert.Equal("previous-key", restarted.GetApiKey());
        Assert.Equal("OpenAI Compatible", restarted.Profiles[0].Name);
    }

    [Fact]
    public async Task AStaleKeyOnlySaveCannotAffectTheNewlySelectedProfile()
    {
        using var plugin = new OpenAiCompatiblePlugin();
        var host = new TestPluginHostServices();
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("add", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync(Default,
            new Dictionary<string, string>(), "wrong-profile-key", default));
        Assert.Empty(host.Secrets);
    }

    [Fact]
    public async Task RemovingProfileRemovesItsCommittedKeyRevision()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("add", default);
        var id = plugin.ConnectionIdentity;
        await plugin.SaveProfileSettingsAsync(id, new Dictionary<string, string>(), "profile-key", default);
        Assert.Single(host.Secrets);
        await plugin.ExecuteSettingsActionAsync(id + "/delete", default);
        Assert.Empty(host.Secrets);
    }

    [Fact]
    public async Task ProfileSaveIsAtomicAndScopedAcrossRestart()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.SaveTextSettingAsync(Default + "/url", "http://localhost:1111", default);
        await plugin.ExecuteSettingsActionAsync("add", default);
        var id = plugin.ConnectionIdentity;
        await plugin.SaveProfileSettingsAsync(new Dictionary<string, string>
        {
            [id + "/name"] = "LM Studio",
            [id + "/url"] = "http://localhost:1234/v1/",
            [id + "/transcription"] = "local-whisper",
            [id + "/text"] = "local-chat",
            [id + "/timeout"] = "600"
        }, default);
        Assert.Equal("http://localhost:1111", plugin.Profiles.Single(p => p.Id == Default).BaseUrl);
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        var saved = restarted.Profiles.Single(p => p.Id == id);
        Assert.Equal("LM Studio", saved.Name);
        Assert.Equal("http://localhost:1234", saved.BaseUrl);
        Assert.Equal("local-whisper", saved.SelectedModelId);
        Assert.Equal("local-chat", saved.SelectedLlmModelId);
        Assert.Equal(600, saved.LlmRequestTimeoutSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrFailedProfileSaveRetainsAllPreviousValues(bool failWrite)
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        var saved = host.GetRawSettingJson("profiles");
        host.FailWrites = failWrite;
        await Assert.ThrowsAnyAsync<Exception>(() => plugin.SaveProfileSettingsAsync(new Dictionary<string, string>
        {
            [Default + "/name"] = "Unsaved name",
            [Default + "/url"] = "http://localhost:1234",
            [Default + "/timeout"] = failWrite ? "600" : "invalid"
        }, default));
        Assert.Equal("OpenAI Compatible", plugin.Profiles[0].Name);
        Assert.Equal("", plugin.Profiles[0].BaseUrl);
        Assert.Equal(saved, host.GetRawSettingJson("profiles"));
    }

    [Fact]
    public async Task ProfileSaveRejectsMixedProfileFieldsBeforePersisting()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("add", default);
        var saved = host.GetRawSettingJson("profiles");
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync(new Dictionary<string, string>
        {
            [plugin.ConnectionIdentity + "/name"] = "Not committed",
            [Default + "/url"] = "https://wrong.invalid"
        }, default));
        Assert.Equal(saved, host.GetRawSettingJson("profiles"));
        Assert.DoesNotContain(plugin.Profiles, p => p.Name == "Not committed");
    }

    [Fact]
    public async Task NewProfilesHaveDistinctNamesAndExposeTheSelectedEditorActions()
    {
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(new TestPluginHostServices());
        Assert.Null(plugin.RemoveProfileActionId);
        await plugin.ExecuteSettingsActionAsync(plugin.AddProfileActionId!, default);
        await plugin.ExecuteSettingsActionAsync(plugin.AddProfileActionId!, default);
        Assert.Equal(3, plugin.Profiles.Select(p => p.Name).Distinct().Count());
        var selector = plugin.TextSettings.Single(f => f.Id == plugin.ProfileSelectorId);
        Assert.Equal(plugin.ConnectionIdentity, selector.Value);
        Assert.Equal(plugin.Profiles.Last().DisplayName, selector.Choices.Single(c => c.Value == selector.Value).Title);
        Assert.Equal(plugin.ConnectionIdentity + "/delete", plugin.RemoveProfileActionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalTranscriptionOmitsAuthenticationAndUsesTheRequestedEndpoint(bool translate)
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(translate ? "/v1/audio/translations" : "/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            Assert.Contains("local-whisper", body!);
            return JsonResponse("""{"text":"Local result","language":"en"}""");
        })));
        await plugin.ActivateAsync(new TestPluginHostServices());
        plugin.SetBaseUrl("http://localhost:8080");
        plugin.SelectModel("local-whisper");
        Assert.Equal("Local result", (await plugin.TranscribeAsync([0, 0], "de", translate, null, default)).Text);
    }

    [Fact]
    public async Task FreshPortableProfileDoesNotImportFlatLegacySettings()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "https://legacy.invalid");
        host.SetSetting("selectedModel", "old-model");
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        Assert.False(plugin.IsConfigured);
        Assert.Null(plugin.SelectedModelId);
        Assert.Equal("https://legacy.invalid", host.GetSetting<string>("baseUrl"));
        Assert.Equal("old-model", host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task ProfileSelectionScopesKeysAndRejectsStaleFields()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        var keys = (IApiKeyPlugin)plugin;
        await keys.SetApiKeyAsync("default-token");
        await plugin.ExecuteSettingsActionAsync("add", default);
        var additional = plugin.ConnectionIdentity;
        Assert.NotEqual(Default, additional);
        Assert.False(keys.IsConfigured);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync(Default + "/url", "https://wrong.invalid", default));
        await keys.SetApiKeyAsync("additional-token");
        Assert.True(keys.IsConfigured);
        await plugin.SaveTextSettingAsync(additional + "/url", "http://localhost:11434/v1/", default);
        await plugin.SaveTextSettingAsync(additional + "/text", "custom-model", default);
        Assert.DoesNotContain("token", host.GetRawSettingJson("profiles"));
        await plugin.SaveTextSettingAsync("profile", Default, default);
        Assert.True(keys.IsConfigured);
        await keys.SetApiKeyAsync("");
        Assert.Null(plugin.GetApiKey(Default));
        Assert.Equal("additional-token", plugin.GetApiKey(additional));
        await plugin.SaveTextSettingAsync("profile", additional, default);
        await plugin.ExecuteSettingsActionAsync(additional + "/delete", default);
        Assert.Equal(Default, plugin.ConnectionIdentity);
        Assert.Single(plugin.Profiles);
        Assert.Empty(host.Secrets);
    }

    [Fact]
    public async Task FailedWritesPreserveFieldsKeysAndHostModelSelection()
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("saved-key");
        plugin.SelectModel("saved-model");
        host.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => ((IApiKeyPlugin)plugin).SetApiKeyAsync("replacement"));
        Assert.Equal("saved-key", plugin.GetApiKey());
        Assert.Throws<IOException>(() => plugin.SelectModel("replacement"));
        Assert.Equal("saved-model", plugin.SelectedModelId);
        foreach (var (field, value) in new[] { ("name", "Renamed"), ("url", "https://server.invalid"), ("transcription", "new-model"), ("text", "text-model"), ("thinking", "on"), ("timeout", "3600") })
        {
            var id = Default + "/" + field;
            var previous = plugin.TextSettings.Single(f => f.Id == id).Value;
            await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync(id, value, default));
            Assert.Equal(previous, plugin.TextSettings.Single(f => f.Id == id).Value);
        }
        await Assert.ThrowsAsync<IOException>(() => plugin.ExecuteSettingsActionAsync("add", default));
        Assert.Single(plugin.Profiles);
    }

    [Theory]
    [InlineData("file:///tmp/test")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?key=secret")]
    [InlineData("https://example.com#fragment")]
    public async Task ServerSettingRejectsUnsupportedOrCredentialBearingUrls(string url)
    {
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(new TestPluginHostServices());
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync(Default + "/url", url, default));
        Assert.False(plugin.IsConfigured);
    }

    [Fact]
    public async Task RefreshRetainsManualModelsAndKeepsCatalogOnFailure()
    {
        var succeed = true;
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal("http://localhost:11434/v1/models", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            return succeed ? JsonResponse("""{"data":[{"id":"catalog-model"}]}""") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        })));
        await plugin.ActivateAsync(new TestPluginHostServices());
        await plugin.SaveTextSettingAsync(Default + "/url", "http://localhost:11434/v1", default);
        await plugin.SaveTextSettingAsync(Default + "/transcription", "manual-audio", default);
        await plugin.SaveTextSettingAsync(Default + "/text", "manual-text", default);
        await plugin.ExecuteSettingsActionAsync(Default + "/refresh", default);
        Assert.Contains(plugin.TranscriptionModels, m => m.Id == "manual-audio");
        Assert.Contains(plugin.SupportedModels, m => m.Id == "manual-text");
        Assert.Contains(plugin.SupportedModels, m => m.Id == "catalog-model");
        succeed = false;
        await plugin.ExecuteSettingsActionAsync(Default + "/refresh", default);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "catalog-model");
    }

    [Fact]
    public async Task CancelledConfigurationDoesNotMutateOrFetch()
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((_, _) => throw new InvalidOperationException("Unexpected request"))));
        await plugin.ActivateAsync(new TestPluginHostServices());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExecuteSettingsActionAsync("add", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.SaveTextSettingAsync(Default + "/name", "Changed", cancellation.Token));
        Assert.Single(plugin.Profiles);
        Assert.Equal("OpenAI Compatible", plugin.Profiles[0].Name);
    }
}
