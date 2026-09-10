using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    private async Task<PortablePluginStore> SpeechStore()
    {
        var bundles = Path.Combine(_root, "speech-bundles");
        Package(bundles, Id, typeof(TtsProbePlugin));
        var store = new PortablePluginStore(Path.Combine(_root, "speech-store"), Version, _http);
        await store.InitializeAsync(bundles); return store;
    }

    [Fact]
    public async Task SpeechDisableCancelsPlaybackAndRetainsPackageUntilStopDrains()
    {
        var store = await SpeechStore(); await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Single(registry.TtsProviders);
        Host(Id).SetSetting("Hold", true);
        var speech = registry.SpeakAsync(Id, new("hello") { VoiceId = "voice", OutputDeviceId = "endpoint" }, default);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disable = registry.SetEnabledAsync(Id, false);
        Assert.Empty(registry.TtsProviders); Assert.False(disable.IsCompleted);
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Host(Id).Release.TrySetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speech);
        Assert.Null(await disable);
        Assert.Equal("voice", Host(Id).GetSetting<string>("voice"));
        Assert.Equal("endpoint", Host(Id).GetSetting<string>("output"));
        Assert.Equal(1, Host(Id).GetSetting<int>("stops"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task SpeechCompletionBeforeSubscriptionStillReportsPlaybackFailureAndStops()
    {
        var store = await SpeechStore(); await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("CompletedSpeech", true); Host(Id).SetSetting("FailedSpeech", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SpeakAsync(Id, new("hello"), default));
        Assert.Equal(1, Host(Id).GetSetting<int>("stops"));
    }
}
