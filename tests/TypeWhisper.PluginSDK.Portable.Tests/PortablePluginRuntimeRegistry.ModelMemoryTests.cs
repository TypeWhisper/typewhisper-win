using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    private readonly ManualTimeProvider _time = new();

    private async Task<PortablePluginStore> LocalLlmStore()
    {
        var bundles = Path.Join(_root, "bundles");
        Package(bundles, Id, typeof(LocalLlmProbePlugin), isLocal: true);
        var store = new PortablePluginStore(Path.Join(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        return store;
    }

    private async Task<PortablePluginRuntimeRegistry> LocalLlmRegistry(PortablePluginStore? store = null)
    {
        store ??= await LocalLlmStore();
        var registry = new PortablePluginRuntimeRegistry(store, Version, id => Host(id)) { IdleUnloadPolicy = new(60, _time) };
        await registry.InitializeAsync();
        if (registry.LlmProviders.Count == 0) Assert.Null(await registry.SetEnabledAsync(Id, true));
        return registry;
    }

    // A queued capability refresh can hold the runtime when the idle delay ends; the release then retries.
    private async Task AdvanceUntilReleasedAsync(int unloads = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Host(Id).GetSetting<int>("unloads") < unloads)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The idle model was not released.");
            _time.Advance(ModelIdleTimer.RetryDelay);
            await Task.Delay(10);
        }
    }

    private static Task<string> Process(PortablePluginRuntimeRegistry registry, string model = "small") =>
        registry.UseLlmAsync(registry.LlmProviders.Single().SelectionId, (provider, token) => provider.ProcessAsync("", "text", model, token));

    [Fact]
    public async Task AnIdleLocalTextModelIsReleasedAndLoadsAgainOnTheNextRequest()
    {
        await using var registry = await LocalLlmRegistry();
        await registry.LoadLocalLlmModelAsync(Id, "small");
        Assert.Equal("small:text", await Process(registry));
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, Host(Id).GetSetting<int>("unloads"));
        await AdvanceUntilReleasedAsync();
        await registry.RefreshCapabilitiesAsync();
        // Workflows keep seeing the remembered model while its memory is released.
        var snapshot = registry.LlmProviders.Single();
        Assert.True(snapshot.Ready);
        Assert.Equal("small", Assert.Single(snapshot.Models).Id);
        Assert.Equal("small", registry.RestorableLocalLlmModel(Id));
        Assert.Equal("small:text", await Process(registry));
        Assert.Equal(2, Host(Id).GetSetting<int>("loads"));
    }

    [Fact]
    public async Task AnIdleReleaseWaitsForRunningPluginOperations()
    {
        await using var registry = await LocalLlmRegistry();
        await registry.LoadLocalLlmModelAsync(Id, "small");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = registry.UseConfigurationAsync(Id, (_, _) => release.Task);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, Host(Id).GetSetting<int>("unloads"));
        release.SetResult(true);
        await running;
        await AdvanceUntilReleasedAsync();
    }

    [Fact]
    public async Task AnExplicitUnloadForgetsTheModel()
    {
        await using var registry = await LocalLlmRegistry();
        await registry.LoadLocalLlmModelAsync(Id, "small");
        await registry.UnloadLocalLlmModelAsync(Id, "small");
        await registry.RefreshCapabilitiesAsync();
        Assert.Null(registry.RestorableLocalLlmModel(Id));
        var snapshot = registry.LlmProviders.Single();
        Assert.False(snapshot.Ready);
        Assert.Empty(snapshot.Models);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Process(registry));
        Assert.Equal(1, Host(Id).GetSetting<int>("loads"));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, Host(Id).GetSetting<int>("unloads"));
    }

    [Fact]
    public async Task RemovingTheRememberedModelForgetsIt()
    {
        await using var registry = await LocalLlmRegistry();
        await registry.LoadLocalLlmModelAsync(Id, "small");
        await registry.RemoveLocalLlmModelAsync(Id, "small");
        Assert.Null(registry.RestorableLocalLlmModel(Id));
    }

    [Fact]
    public async Task TheRememberedModelLoadsOnFirstUseAfterARestart()
    {
        var store = await LocalLlmStore();
        await using (var first = await LocalLlmRegistry(store))
            await first.LoadLocalLlmModelAsync(Id, "large");
        await using var registry = await LocalLlmRegistry(store);
        var snapshot = registry.LlmProviders.Single();
        Assert.True(snapshot.Ready);
        Assert.Equal("large", Assert.Single(snapshot.Models).Id);
        Assert.Equal(1, Host(Id).GetSetting<int>("loads"));
        Assert.Equal("large:text", await Process(registry, "large"));
        Assert.Equal(2, Host(Id).GetSetting<int>("loads"));
    }

    [Fact]
    public async Task AModelThatNoLongerLoadsStopsAdvertisingItself()
    {
        await using var registry = await LocalLlmRegistry();
        await registry.LoadLocalLlmModelAsync(Id, "small");
        _time.Advance(TimeSpan.FromSeconds(60));
        await AdvanceUntilReleasedAsync();
        Host(Id).SetSetting("FailLoad", true);
        await Assert.ThrowsAsync<IOException>(() => Process(registry));
        await registry.RefreshCapabilitiesAsync();
        Assert.Null(registry.RestorableLocalLlmModel(Id));
        Assert.False(registry.LlmProviders.Single().Ready);
    }
}
