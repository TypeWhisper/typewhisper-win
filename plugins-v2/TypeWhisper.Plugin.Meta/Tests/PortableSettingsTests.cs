using TypeWhisper.Plugin.Meta;
using TypeWhisper.PluginSDK;
namespace PortableMigration.Tests;

public sealed class PortableSettingsTests
{
    [Fact]
    public async Task SavedSettingsSurviveRestartAndInvalidChangesLeavePreviousValues()
    {
        using var fixture = new PortableFixture();
        using var plugin = new MetaPlugin();
        await plugin.ActivateAsync(fixture.Host);
        var settings = (IPluginTextSettings)plugin;
        await settings.SaveTextSettingAsync("llmModel", "muse-spark-1.1", default);
        await settings.SaveTextSettingAsync("reasoning", "minimal", default);
        await settings.SaveTextSettingAsync("diarization", "true", default);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveTextSettingAsync("llmModel", "unknown-model", default));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveTextSettingAsync("reasoning", "invalid", default));
        await plugin.DeactivateAsync();
        using var restart = new MetaPlugin();
        await restart.ActivateAsync(fixture.Host);
        Assert.Equal("muse-spark-1.1", restart.SelectedLlmModelId);
        Assert.Equal("minimal", restart.ReasoningEffort);
        Assert.True(restart.SpeakerDiarizationEnabled);
        await restart.DeactivateAsync();
    }
}
