public sealed partial class PortablePluginRuntimeRegistryTests
{
    [Fact]
    public async Task ExplicitModelUnloadKeepsPackageEnabledAndClearsNativeRemovalBlock()
    {
        await using var registry = await RemovableModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        await registry.SelectModelAsync(model);
        Host(Id).SetSetting("NoSelectedModel", true);
        Assert.NotNull(Assert.Single(await registry.GetModelStatesAsync(Id)).RemovalBlockedReason);
        await registry.UnloadModelAsync(Id);
        Assert.Equal(1, Host(Id).GetSetting<int>("unloadCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Assert.True(Assert.Single(registry.Snapshot()).Enabled);
        Assert.Null(Assert.Single(await registry.GetModelStatesAsync(Id)).RemovalBlockedReason);
        await registry.RemoveModelAsync(Assert.Single(await registry.GetModelStatesAsync(Id)));
        Assert.False(Assert.Single(await registry.GetModelStatesAsync(Id)).Downloaded);
    }

    [Fact]
    public async Task ExplicitCloudUnloadIsUnsupportedAndDoesNotCallSdkDefault()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("LocalModels", false);
        await Assert.ThrowsAsync<NotSupportedException>(() => registry.UnloadModelAsync(Id));
        Assert.Equal(0, Host(Id).GetSetting<int>("unloadCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
    }
}
