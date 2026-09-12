using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public partial class OpenAiCompatiblePluginTests
{
    private const string Default = OpenAiCompatiblePlugin.DefaultProfileId;

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
