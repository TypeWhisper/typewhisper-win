using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalLlmRequestsEnforcePackageLocalityBeforeInvokingProvider(bool local)
    {
        var bundles = Path.Combine(_root, "bundles");
        Package(bundles, Id, typeof(RuntimeProbePlugin), isLocal: local);
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        Host(Id).SetSetting("ExtraRole", true);
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Equal(2, registry.LlmProviders.Count);
        foreach (var snapshot in registry.LlmProviders)
        {
            Assert.Equal(local, snapshot.IsLocal);
            var called = false;
            var request = registry.UseLocalLlmAsync(snapshot.SelectionId, (provider, token) =>
            {
                called = true;
                return provider.ProcessAsync("prompt", "result", "llm", token);
            });
            if (local) Assert.Equal("result", await request);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => request);
            Assert.Equal(local, called);
        }
        // Ordinary workflows can still deliberately select cloud providers.
        Assert.Equal("cloud allowed", await registry.UseLlmAsync(registry.LlmProviders[0].SelectionId,
            (provider, token) => provider.ProcessAsync("prompt", "cloud allowed", "llm", token)));
    }
}
