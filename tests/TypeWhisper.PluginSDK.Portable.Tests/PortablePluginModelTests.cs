using TypeWhisper.PluginHost;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    private async Task<PortablePluginRuntimeRegistry> LocalModelRegistry()
    {
        var store = await Store();
        Host(Id).SetSetting("LocalModels", true);
        Host(Id).SetSetting("NotReady", true);
        var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        return registry;
    }

    [Fact]
    public async Task CloudModelSelectionRechecksReadinessWithoutLoadingAssets()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("LocalModels", false);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SelectModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("selectCalls"));
        Host(Id).SetSetting("NotReady", false);
        await registry.SelectModelAsync(model);
        Assert.Equal(1, Host(Id).GetSetting<int>("selectCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("loadCalls"));
    }

    [Fact]
    public async Task NotReadyModelCanDownloadWithoutSelectingOrLoadingAndSnapshotRemainsUnchanged()
    {
        await using var registry = await LocalModelRegistry();
        Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        Assert.True(model.SupportsDownload); Assert.False(model.Downloaded);
        var progress = new List<double>();
        await registry.DownloadModelAsync(model, new ModelProgress(progress.Add));
        Assert.Equal(new[] { 0.25, 1 }, progress);
        Assert.False(model.Downloaded);
        Assert.True(Assert.Single(await registry.GetModelStatesAsync(Id)).Downloaded);
        Assert.Equal(0, Host(Id).GetSetting<int>("selectCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("loadCalls"));
    }

    [Fact]
    public async Task RequirementsAreDetachedAndRecheckedImmediatelyBeforeDownload()
    {
        await using var registry = await LocalModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        Assert.True(Assert.Single(model.Requirements).IsSatisfied);
        Host(Id).SetSetting("BlockedRequirement", true);
        var current = Assert.Single(await registry.GetModelStatesAsync(Id));
        Assert.False(Assert.Single(current.Requirements).IsSatisfied);
        Assert.True(Assert.Single(model.Requirements).IsSatisfied);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DownloadModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("downloadCalls"));
    }

    [Theory]
    [InlineData("DownloadThrows", typeof(IOException))]
    [InlineData("MissingAfterDownload", typeof(InvalidOperationException))]
    public async Task FailureOrMissingAssetsCannotReportDownloaded(string setting, Type error)
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting(setting, true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var exception = await Record.ExceptionAsync(() => registry.DownloadModelAsync(model));
        Assert.NotNull(exception); Assert.Equal(error, exception.GetType());
        Assert.False(Assert.Single(await registry.GetModelStatesAsync(Id)).Downloaded);
        Assert.Equal(0, Host(Id).GetSetting<int>("selectCalls"));
    }

    [Fact]
    public async Task StaleActivationAndForgedEngineIdentityAreRejectedBeforeDownload()
    {
        await using var registry = await LocalModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.DownloadModelAsync(model with { EngineIdentity = Guid.NewGuid() }));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.DownloadModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("downloadCalls"));
    }

    [Fact]
    public async Task DisableDrainsDownloadBeforePackageDisposalAndRejectsLateCompletion()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Hold", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var download = registry.DownloadModelAsync(model);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disable = registry.SetEnabledAsync(Id, false);
        Assert.False(disable.IsCompleted);
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Host(Id).Release.TrySetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Null(await disable);
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task ExplicitSelectionRequiresAssetsAndThenLoadsWithoutDownloading()
    {
        await using var registry = await LocalModelRegistry();
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SelectModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("loadCalls"));
        Host(Id).SetSetting("Downloaded", true);
        await registry.SelectModelAsync(model);
        Assert.Equal(1, Host(Id).GetSetting<int>("loadCalls"));
        Assert.Equal(1, Host(Id).GetSetting<int>("selectCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("downloadCalls"));
    }

    [Fact]
    public async Task ExplicitSelectionRejectsDifferentEngineModelAndActivation()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Downloaded", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.SelectModelAsync(model with { EngineIdentity = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SelectModelAsync(model with { ModelId = "missing" }));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.SelectModelAsync(model));
        Assert.Equal(0, Host(Id).GetSetting<int>("loadCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("selectCalls"));
    }

    [Fact]
    public async Task CanceledNativeLoadDrainsWithoutSelectingTheModel()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Downloaded", true);
        Host(Id).SetSetting("HoldLoad", true);
        Host(Id).SetSetting("Hold", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        using var cancellation = new CancellationTokenSource();
        var selection = registry.SelectModelAsync(model, cancellation.Token);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.False(selection.IsCompleted);
        Host(Id).Release.TrySetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Equal(1, Host(Id).GetSetting<int>("loadCalls"));
        Assert.Equal(0, Host(Id).GetSetting<int>("selectCalls"));
    }

    [Fact]
    public async Task RegistryShutdownDrainsActualDownloadBeforeDisposingItsPackage()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Hold", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var download = registry.DownloadModelAsync(model);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var shutdown = registry.DisposeAsync().AsTask();
        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
            Host(Id).Release.TrySetResult(null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            await shutdown;
            Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
        }
        finally { Host(Id).Release.TrySetResult(null); await shutdown; }
    }

    [Fact]
    public async Task UninstallRetainsRegistrationUntilDownloadAndDisableHaveDrained()
    {
        var store = await Store();
        Host(Id).SetSetting("LocalModels", true);
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var download = registry.DownloadModelAsync(model);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The store contract requires the host to disable and drain before removing its receipt.
        async Task UninstallAfterDrain()
        {
            Assert.Null(await registry.SetEnabledAsync(Id, false));
            await store.UninstallAsync(Id);
        }
        var uninstall = UninstallAfterDrain();
        try
        {
            Assert.False(uninstall.IsCompleted); Assert.True(store.IsInstalled(Id));
            Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
            Host(Id).Release.TrySetResult(null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            await uninstall;
            Assert.False(store.IsInstalled(Id));
            Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
            Assert.True(Host(Id).GetSetting<bool>("Downloaded"));
        }
        finally { Host(Id).Release.TrySetResult(null); await uninstall; }
    }

    [Fact]
    public async Task SelectionCanAwaitWorkerNotificationThatReadsRegistryState()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Downloaded", true);
        Host(Id).SetSetting("SelectWorkerNotification", true);
        var notifications = 0;
        Host(Id).OnCapabilitiesChanged = () =>
        {
            Assert.NotEmpty(registry.Snapshot());
            Interlocked.Increment(ref notifications);
        };
        try
        {
            var model = Assert.Single(await registry.GetModelStatesAsync(Id));
            await registry.SelectModelAsync(model);
            Assert.Equal(1, notifications);
            Assert.Equal(1, Host(Id).GetSetting<int>("selectCalls"));
        }
        finally { Host(Id).OnCapabilitiesChanged = null; }
    }

    [Fact]
    public async Task PublishedModelStatesRemainDetachedAndDoNotUseProviderReadinessAsAssetStatus()
    {
        await using var registry = await LocalModelRegistry();
        var before = Assert.Single(registry.TranscriptionProviders);
        Assert.False(before.Ready);
        var model = Assert.Single(before.ModelStates);
        Assert.False(model.Downloaded);
        Host(Id).SetSetting("Downloaded", true);
        await registry.RefreshCapabilitiesAsync();
        var after = Assert.Single(registry.TranscriptionProviders);
        Assert.False(after.Ready);
        Assert.True(Assert.Single(after.ModelStates).Downloaded);
        Assert.False(Assert.Single(before.ModelStates).Downloaded);
        await registry.SelectModelAsync(Assert.Single(after.ModelStates));
        Assert.Equal(1, Host(Id).GetSetting<int>("selectCalls"));
    }

    [Fact]
    public async Task PublishedModelIdentitySurvivesFailedDisableWithoutAcceptingOldSnapshot()
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Downloaded", true);
        await registry.RefreshCapabilitiesAsync();
        var before = Assert.Single(Assert.Single(registry.TranscriptionProviders).ModelStates);
        Host(Id).FailEnabledWrites = true;
        Assert.NotNull(await registry.SetEnabledAsync(Id, false));
        Host(Id).FailEnabledWrites = false;
        var current = Assert.Single(Assert.Single(registry.TranscriptionProviders).ModelStates);
        Assert.NotEqual(before.Generation, current.Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.SelectModelAsync(before));
        await registry.SelectModelAsync(current);
        Assert.Equal(1, Host(Id).GetSetting<int>("selectCalls"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelVersionGetterCanAwaitWorkerReadingRegistryDuringDownloadOrSelection(bool select)
    {
        await using var registry = await LocalModelRegistry();
        Host(Id).SetSetting("Downloaded", select);
        var model = Assert.Single(await registry.GetModelStatesAsync(Id));
        var notifications = 0;
        Host(Id).OnCapabilitiesChanged = () =>
        {
            Assert.NotEmpty(registry.Snapshot());
            Interlocked.Increment(ref notifications);
        };
        Host(Id).SetSetting("VersionWorkerNotification", true);
        try
        {
            if (select) await registry.SelectModelAsync(model);
            else await registry.DownloadModelAsync(model);
            Assert.True(notifications > 0);
            Assert.Equal(select ? 1 : 0, Host(Id).GetSetting<int>("selectCalls"));
            Assert.Equal(select ? 0 : 1, Host(Id).GetSetting<int>("downloadCalls"));
        }
        finally
        {
            Host(Id).SetSetting("VersionWorkerNotification", false);
            Host(Id).OnCapabilitiesChanged = null;
        }
    }

    private sealed class ModelProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
