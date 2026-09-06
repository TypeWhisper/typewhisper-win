using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class PluginManagementTests
{
    private const string PluginId = "com.test.engine";
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "plugin-management-fixture");
    private static string PackagePath => Path.Combine(Root, PluginId);
    private static InstalledPluginPackage Package(string? error = null, string? directory = null) => new(directory ?? PackagePath,
        new PluginManifest { Id = PluginId, Name = "Engine", Version = "1.0", AssemblyName = "engine.dll", PluginClass = "Engine" }, error);

    private sealed class Runtime
    {
        public bool Enabled;
        public bool Busy;
        public bool CanChange = true;
        public int Calls;
        public Func<bool, Task<string?>>? Change;
        public ManagedPluginBinding Binding => new(PluginId, () => Enabled, () => Busy, () => null, async enabled =>
        {
            Calls++;
            if (Change is not null) return await Change(enabled);
            Enabled = enabled; return null;
        });
        public PluginManagementController Controller(Func<Task<IReadOnlyList<InstalledPluginPackage>>>? scan = null) =>
            new(Root, [Binding], () => CanChange, scan ?? (() => Task.FromResult<IReadOnlyList<InstalledPluginPackage>>([Package()])));
    }

    [Fact]
    public async Task DiscoveryDoesNotActivateAndReflectsRuntimeChanges()
    {
        var runtime = new Runtime(); var controller = runtime.Controller();
        await controller.RefreshAsync();
        Assert.Equal(0, runtime.Calls);
        Assert.False(Assert.Single(controller.Snapshot()).Enabled);
        runtime.Enabled = true;
        Assert.True(Assert.Single(controller.Snapshot()).Enabled);
        Assert.Null(await controller.SetEnabledAsync(PackagePath, false));
        Assert.False(runtime.Enabled);
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task CurrentCaptureOrProcessingStateIsRecheckedAtInvocation()
    {
        var runtime = new Runtime(); var controller = runtime.Controller(); await controller.RefreshAsync();
        Assert.True(Assert.Single(controller.Snapshot()).CanToggle);
        runtime.CanChange = false;
        Assert.NotNull(await controller.SetEnabledAsync(PackagePath, true));
        Assert.False(Assert.Single(controller.Snapshot()).CanToggle);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task TwoClicksCannotStartConcurrentPluginOperations()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        runtime.Change = async enabled => { entered.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(5)); runtime.Enabled = enabled; return null; };
        var controller = runtime.Controller(); await controller.RefreshAsync();
        var first = controller.SetEnabledAsync(PackagePath, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(controller.Snapshot()).Busy);
        Assert.False(Assert.Single(controller.Snapshot()).CanToggle);
        Assert.NotNull(await controller.SetEnabledAsync(PackagePath, false));
        finish.SetResult(); Assert.Null(await first);
        Assert.True(runtime.Enabled); Assert.Equal(1, runtime.Calls);
        Assert.True(Assert.Single(controller.Snapshot()).CanToggle);
    }

    [Fact]
    public async Task FailureIsVisibleAndRetryClearsTheError()
    {
        var runtime = new Runtime { Change = _ => throw new IOException("model missing") };
        var controller = runtime.Controller(); await controller.RefreshAsync();
        Assert.Contains("model missing", await controller.SetEnabledAsync(PackagePath, true));
        Assert.False(runtime.Enabled);
        Assert.Contains("model missing", Assert.Single(controller.Snapshot()).Error);
        runtime.Change = null;
        Assert.Null(await controller.SetEnabledAsync(PackagePath, true));
        Assert.True(runtime.Enabled); Assert.Null(Assert.Single(controller.Snapshot()).Error);
    }

    [Fact]
    public async Task SuccessFromAnotherSurfaceClearsPreviousOperationError()
    {
        var runtime = new Runtime { Change = _ => throw new IOException("model missing") };
        var controller = runtime.Controller(); await controller.RefreshAsync();
        Assert.NotNull(await controller.SetEnabledAsync(PackagePath, true));
        runtime.Change = null;
        await runtime.Binding.ChangeEnabledAsync(true);
        var state = Assert.Single(controller.Snapshot());
        Assert.True(state.Enabled);
        Assert.Null(state.Error);
    }

    [Fact]
    public async Task UnknownCorruptOrDifferentDirectoryPackagesCannotInvokeRegisteredRuntime()
    {
        foreach (var package in new[] { Package("corrupt"), Package(directory: Path.Combine(Root, "different")) })
        {
            var runtime = new Runtime();
            var controller = runtime.Controller(() => Task.FromResult<IReadOnlyList<InstalledPluginPackage>>([package]));
            await controller.RefreshAsync();
            Assert.NotNull(await controller.SetEnabledAsync(package.Directory, true));
            Assert.False(Assert.Single(controller.Snapshot()).CanToggle);
            Assert.Equal(0, runtime.Calls);
        }
        var empty = new Runtime(); var missing = empty.Controller(); await missing.RefreshAsync();
        Assert.NotNull(await missing.SetEnabledAsync("unknown", true)); Assert.Equal(0, empty.Calls);
    }

    [Fact]
    public async Task BusyRuntimeFromAnotherSurfaceCannotBeInvoked()
    {
        var runtime = new Runtime { Busy = true }; var controller = runtime.Controller(); await controller.RefreshAsync();
        Assert.True(Assert.Single(controller.Snapshot()).Busy);
        Assert.NotNull(await controller.SetEnabledAsync(PackagePath, true));
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task SlowerOldRefreshCannotOverwriteNewerInventory()
    {
        var old = new TaskCompletionSource<IReadOnlyList<InstalledPluginPackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var runtime = new Runtime();
        var controller = runtime.Controller(() => ++calls == 1 ? old.Task : Task.FromResult<IReadOnlyList<InstalledPluginPackage>>([]));
        var first = controller.RefreshAsync(); await controller.RefreshAsync();
        old.SetResult([Package()]); await first;
        Assert.Empty(controller.Snapshot());
    }

    [Fact]
    public async Task DiscoveryFailureProducesReviewableErrorInsteadOfSuccessOrCrash()
    {
        var controller = new Runtime().Controller(() => throw new IOException("folder unavailable"));
        await controller.RefreshAsync();
        var state = Assert.Single(controller.Snapshot());
        Assert.Contains("folder unavailable", state.Error);
        Assert.False(state.CanToggle); Assert.False(state.Enabled);
    }
}
