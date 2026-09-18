using TypeWhisper.Plugin.SupertonicTts;
using TypeWhisper.PluginSDK;

namespace PortableMigration.Tests;

public sealed class PortableLicenseTests
{
    [Fact]
    public async Task DownloadRequiresExplicitLicenseAcceptanceThroughPortableSettings()
    {
        using var fixture = new PortableFixture();
        using var plugin = new SupertonicTtsPlugin();
        await plugin.ActivateAsync(fixture.Host);
        try
        {
            IPluginTextSettings settings = plugin;
            IPluginSettingsActions actions = plugin;
            Assert.Equal("false", Assert.Single(settings.TextSettings, f => f.Id == "license").Value);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExecuteSettingsActionAsync("download", default));
            await settings.SaveTextSettingAsync("license", "true", default);
            Assert.True(plugin.HasAcceptedModelLicense);
            await settings.SaveTextSettingAsync("license", "false", default);
            Assert.False(plugin.HasAcceptedModelLicense);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExecuteSettingsActionAsync("download", default));
        }
        finally { await plugin.DeactivateAsync(); }
    }
}
