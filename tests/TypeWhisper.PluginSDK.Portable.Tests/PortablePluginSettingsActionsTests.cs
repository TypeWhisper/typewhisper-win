using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    [Fact]
    public async Task CancelingInteractiveConfigurationReleasesUnrelatedTranscription()
    {
        var store = await Store(second: true);
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Null(await registry.SetEnabledAsync(OtherId, true));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = registry.UseConfigurationAsync(Id, async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }, lifetime.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var transcription = registry.UseTranscriptionAsync(OtherId,
            (plugin, ct) => plugin.TranscribeAsync([], "de", false, null, ct));
        Assert.False(transcription.IsCompleted);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configuration);
        Assert.Equal("transcribed", (await transcription.WaitAsync(TimeSpan.FromSeconds(10))).Text);
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Assert.Equal(0, Host(OtherId).GetSetting<int>("disposals"));
    }

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
