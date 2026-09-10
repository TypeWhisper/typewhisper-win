using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    [Fact]
    public async Task ActionsOnlyPluginExposesSettingsPageAndExecutesActionUnderLease()
    {
        var bundles = Path.Combine(_root, "actions-bundles");
        Package(bundles, Id, typeof(SettingsActionsProbePlugin));
        var store = new PortablePluginStore(Path.Combine(_root, "actions-store"), Version, _http);
        await store.InitializeAsync(bundles);
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.True(Assert.Single(registry.Snapshot()).HasTextSettings);
        var result = await registry.UseConfigurationAsync(Id, (plugin, ct) =>
        {
            Assert.False(plugin is IPluginTextSettings);
            var actions = Assert.IsAssignableFrom<IPluginSettingsActions>(plugin);
            return actions.ExecuteSettingsActionAsync(Assert.Single(actions.SettingsActions).Id, ct);
        });
        Assert.Equal("Connected", result);
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.False(Assert.Single(registry.Snapshot()).HasTextSettings);
    }
}
