using TypeWhisper.PluginHost;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    private async Task<PortablePluginRuntimeRegistry> RemovableModelRegistry()
    {
        var registry = await LocalModelRegistry();
        Host(Id).SetSetting("ModelRemoval", true);
        Host(Id).SetSetting("NoSelectedModel", true);
        Host(Id).SetSetting("Downloaded", true);
        return registry;
    }

    [Fact]
    public async Task RemovalUsesActualFilesWithoutChangingSelectionAndAlreadyMissingIsIdempotent()
    {
        await using var registry = await RemovableModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        Assert.True(model.SupportsRemoval); Assert.Null(model.RemovalBlockedReason);
        await registry.RemoveModelAsync(model);
        Assert.True(model.Downloaded);
        Assert.False(Assert.Single(await registry.GetModelStatesAsync(Id)).Downloaded);
        await registry.RemoveModelAsync(model);
        Assert.Equal(1, Host(Id).GetSetting<int>("removeCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("selectCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("unloadCalls"));
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("unsupported")]
    [InlineData("unknown")]
    [InlineData("identity")]
    [InlineData("version")]
    public async Task RemovalRejectsCurrentSafetyFailuresAndForgedSnapshots(string reason)
    {
        await using var registry = await RemovableModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        if (reason == "selected") Host(Id).SetSetting("NoSelectedModel", false);
        if (reason == "unsupported") Host(Id).SetSetting("ModelRemoval", false);
        if (reason == "unknown") model = model with { ModelId = "missing" };
        if (reason == "identity") model = model with { EngineIdentity = Guid.NewGuid() };
        if (reason == "version") model = model with { Version = "9.0.0" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.RemoveModelAsync(model));
        Assert.True(Host(Id).GetSetting<bool>("Downloaded")); Assert.Equal(0, Host(Id).GetSetting<int>("removeCalls"));
        if (reason is "selected" or "unsupported") Assert.NotNull(Assert.Single(await registry.GetModelStatesAsync(Id)).RemovalBlockedReason);
    }

    [Fact]
    public async Task RemovalRejectsPreviousActivationBeforeCallingPlugin()
    {
        await using var registry = await RemovableModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        Assert.Null(await registry.SetEnabledAsync(Id, false)); Assert.Null(await registry.SetEnabledAsync(Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.RemoveModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("removeCalls"));
        await registry.RemoveModelAsync(Assert.Single(await registry.GetModelStatesAsync(Id)));
        Assert.Equal(1, Host(Id).GetSetting<int>("removeCalls"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulOrFailedLoadBlocksRemovalUntilNewActivation(bool fail)
    {
        await using var registry = await RemovableModelRegistry();
        Host(Id).SetSetting("LoadThrows", fail);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        if (fail) await Assert.ThrowsAsync<IOException>(() => registry.SelectModelAsync(model));
        else await registry.SelectModelAsync(model);
        // This fixture reports no selected model, so the separate possibly-loaded protection is exercised.
        Assert.Contains("may still be loaded", Assert.Single(await registry.GetModelStatesAsync(Id)).RemovalBlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RemoveModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("removeCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("unloadCalls"));
        Assert.Null(await registry.SetEnabledAsync(Id, false)); Assert.Null(await registry.SetEnabledAsync(Id, true));
        await registry.RemoveModelAsync(Assert.Single(await registry.GetModelStatesAsync(Id)));
        Assert.Equal(1, Host(Id).GetSetting<int>("removeCalls"));
    }

    [Fact]
    public async Task CanceledLoadAlsoBlocksRemovalOfPossiblyLoadedFiles()
    {
        await using var registry = await RemovableModelRegistry();
        Host(Id).SetSetting("HoldLoad", true); Host(Id).SetSetting("Hold", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        using var cancellation = new CancellationTokenSource();
        var loading = registry.SelectModelAsync(model, cancellation.Token);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel(); Host(Id).Release.TrySetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RemoveModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("removeCalls"));
    }

    [Theory]
    [InlineData("RemoveThrows", typeof(IOException))]
    [InlineData("FilesRemainAfterRemove", typeof(InvalidOperationException))]
    public async Task RemovalFailureOrUnconfirmedFilesCannotReportSuccess(string setting, Type expected)
    {
        await using var registry = await RemovableModelRegistry();
        Host(Id).SetSetting(setting, true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var error = await Record.ExceptionAsync(() => registry.RemoveModelAsync(model));
        Assert.NotNull(error); Assert.Equal(expected, error.GetType());
        Assert.True(Host(Id).GetSetting<bool>("Downloaded"));
    }

    [Fact]
    public async Task DisableDrainsRemovalAndRejectsLateSuccessWithoutClaimingFilesWereRestored()
    {
        await using var registry = await RemovableModelRegistry();
        Host(Id).SetSetting("Hold", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var removal = registry.RemoveModelAsync(model);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disable = registry.SetEnabledAsync(Id, false);
        try
        {
            Assert.False(disable.IsCompleted); Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
            Host(Id).Release.TrySetResult(null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removal);
            Assert.Null(await disable);
            Assert.False(Host(Id).GetSetting<bool>("Downloaded"));
            Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
        }
        finally { Host(Id).Release.TrySetResult(null); await disable; }
    }

    [Fact]
    public async Task RemovalWaitsForExistingPackageOperationAndRechecksSelectionInsideLease()
    {
        await using var registry = await RemovableModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        Host(Id).SetSetting("Hold", true);
        var download = registry.UseLlmAsync(Id, async (provider, ct) => await provider.ProcessAsync("", "", "llm", ct));
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var removal = registry.RemoveModelAsync(model);
        Assert.False(removal.IsCompleted); Assert.Equal(0, Host(Id).GetSetting<int>("removeCalls"));
        Host(Id).SetSetting("NoSelectedModel", false); Host(Id).Release.TrySetResult(null);
        await download;
        await Assert.ThrowsAsync<InvalidOperationException>(() => removal);
        Assert.Equal(0, Host(Id).GetSetting<int>("removeCalls"));
    }
}
