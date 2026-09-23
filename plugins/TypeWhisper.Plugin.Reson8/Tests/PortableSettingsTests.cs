using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;
public partial class Reson8PluginTests
{
    [Fact]
    public async Task FailedKeySaveKeepsTheStoredAndActiveKey()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "old-key";
        using var plugin = new Reson8Plugin(); await plugin.ActivateAsync(host);
        host.FailSecretWrites = true;
        await Assert.ThrowsAsync<System.IO.IOException>(() => ((IApiKeyPlugin)plugin).SetApiKeyAsync("new-key"));
        Assert.Equal("old-key", plugin.ApiKey); Assert.Equal("old-key", host.Secrets["api-key"]);
        await Assert.ThrowsAsync<System.IO.IOException>(() => ((IApiKeyPlugin)plugin).SetApiKeyAsync(""));
        Assert.True(plugin.IsConfigured); Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }
    [Fact]
    public async Task SharedSettingsPersistAcrossActivation()
    {
        var host = new TestPluginHostServices(); using var plugin = new Reson8Plugin(); await plugin.ActivateAsync(host);
        Assert.All(plugin.TextSettings, setting => Assert.False(setting.SaveChoiceOnChange));
        await plugin.SaveTextSettingAsync("baseUrl", "https://proxy.example.test/", default);
        await plugin.SaveTextSettingAsync("authHeader", "X-Api-Key", default);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("https://proxy.example.test", plugin.CustomBaseUrl);
        Assert.Equal("X-Api-Key", plugin.CustomAuthHeader);
        Assert.Contains("fy", plugin.SupportedLanguages);
    }
}
