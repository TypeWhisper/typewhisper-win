using TypeWhisper.WinUI;
using Xunit;

public sealed class VulkanLayerPolicyTests
{
    [Fact]
    public void DisablesImplicitLayersByDefault()
    {
        var environment = new Dictionary<string, string?>();
        Assert.True(Apply(environment));
        Assert.Equal("~implicit~", environment["VK_LOADER_LAYERS_DISABLE"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("~all~")]
    [InlineData("VK_LAYER_VALVE_steam_overlay_*")]
    public void KeepsUserLoaderSetting(string value)
    {
        var environment = new Dictionary<string, string?> { ["VK_LOADER_LAYERS_DISABLE"] = value };
        Assert.False(Apply(environment));
        Assert.Equal(value, environment["VK_LOADER_LAYERS_DISABLE"]);
    }

    [Fact]
    public void OptOutKeepsImplicitLayers()
    {
        var environment = new Dictionary<string, string?> { ["TYPEWHISPER_KEEP_VULKAN_IMPLICIT_LAYERS"] = "1" };
        Assert.False(Apply(environment));
        Assert.False(environment.ContainsKey("VK_LOADER_LAYERS_DISABLE"));
    }

    [Fact]
    public void OtherOptOutValuesStillDisable()
    {
        var environment = new Dictionary<string, string?> { ["TYPEWHISPER_KEEP_VULKAN_IMPLICIT_LAYERS"] = "0" };
        Assert.True(Apply(environment));
        Assert.Equal("~implicit~", environment["VK_LOADER_LAYERS_DISABLE"]);
    }

    private static bool Apply(Dictionary<string, string?> environment) =>
        VulkanLayerPolicy.Apply(name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);
}
