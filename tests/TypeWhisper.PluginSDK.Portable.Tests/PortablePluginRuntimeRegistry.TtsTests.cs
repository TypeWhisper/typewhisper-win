using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.PortableFixture;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloudVoicesRemainAvailableWhenWindowsVoiceEnumerationFails(bool localFails)
    {
        var store = await SpeechStore(); await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        var local = new Moq.Mock<ISpokenFeedbackBackend>();
        if (localFails) local.Setup(backend => backend.GetVoices()).Throws(new InvalidOperationException("No SAPI voices"));
        else local.Setup(backend => backend.GetVoices()).Returns([new SpokenFeedbackVoice("local", "Local voice")]);
        var backend = new PluginSpokenFeedbackBackend(registry, local.Object);

        var voices = backend.GetVoices();
        Assert.Contains(voices, voice => voice.Id == "plugin:" + Id + ":voice");
        Assert.Equal(localFails ? 1 : 2, voices.Count);
        Host(Id).SetSetting("CompletedSpeech", true);
        await backend.SpeakAsync(new("Hello", VoiceId: "plugin:" + Id + ":voice"), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("voice", Host(Id).GetSetting<string>("voice"));
        local.Verify(voice => voice.SpeakAsync(Moq.It.IsAny<SpokenFeedbackRequest>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.Never);
    }

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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speech.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await disable.WaitAsync(TimeSpan.FromSeconds(5)));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SpeakAsync(Id, new("hello"), default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Host(Id).GetSetting<int>("stops"));
    }
}
