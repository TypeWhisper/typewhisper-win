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
            Assert.DoesNotContain(settings.TextSettings, f => f.Id == "license");
            Assert.False(plugin.HasAcceptedModelLicense);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExecuteSettingsActionAsync("download", default));
            await plugin.SetModelDownloadLicenseAcceptanceAsync("supertonic-3", "model-license", true, default);
            Assert.True(plugin.HasAcceptedModelLicense);
            await plugin.SetModelDownloadLicenseAcceptanceAsync("supertonic-3", "model-license", false, default);
            Assert.False(plugin.HasAcceptedModelLicense);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExecuteSettingsActionAsync("download", default));
        }
        finally { await plugin.DeactivateAsync(); }
    }
}
