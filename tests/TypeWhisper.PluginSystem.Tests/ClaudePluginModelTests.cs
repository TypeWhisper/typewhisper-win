using TypeWhisper.Plugin.Claude;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ClaudePluginModelTests
{
    [Fact]
    public void SupportedModels_DefaultIsActiveAndRetiredSonnet4IsNotOffered()
    {
        using var plugin = new ClaudePlugin();

        Assert.Equal("claude-sonnet-5", plugin.SupportedModels[0].Id);
        Assert.DoesNotContain(plugin.SupportedModels, model => model.Id == "claude-sonnet-4-20250514");
    }
}
