using Moq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.PortableFixture;
using Xunit;

public sealed class PortablePluginTests
{
    private static VocabularyRescoreRequest Request() => new(Guid.NewGuid(), "Type whisper", new float[16000], 16000,
        [new("Type", 0, .4), new("whisper", .4, .9)], [new("TypeWhisper")]);

    [Fact]
    public void PortableAssemblyDoesNotReferenceWpfOrWinUI()
    {
        var sdk = typeof(ITypeWhisperPlugin).Assembly;
        Assert.DoesNotContain(sdk.GetReferencedAssemblies(), name => name.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "Microsoft.WinUI");
        Assert.Null(typeof(ITypeWhisperPlugin).GetMethod("CreateSettingsView"));
    }

    [Fact]
    public async Task SeparatePluginAssemblyActivatesUsesSettingsAndDeactivates()
    {
        Assert.NotEqual(typeof(ITypeWhisperPlugin).Assembly, typeof(ContractProbePlugin).Assembly);
        var host = new Mock<IPluginHostServices>();
        host.Setup(x => x.GetSetting<int>("activations")).Returns(2);
        using ITypeWhisperPlugin plugin = new ContractProbePlugin();
        await plugin.ActivateAsync(host.Object);
        host.Verify(x => x.SetSetting("activations", 3), Times.Once);
        host.Verify(x => x.NotifyCapabilitiesChanged(), Times.Once);
        Assert.True(((IVocabularyRescorerPlugin)plugin).IsReady);
        await plugin.DeactivateAsync();
        Assert.False(((IVocabularyRescorerPlugin)plugin).IsReady);
    }

    [Fact]
    public async Task VocabularyRequestCarriesAudioTimingsAndTypeWhisperWithoutForcedReplacement()
    {
        using var plugin = new ContractProbePlugin();
        await plugin.ActivateAsync(Mock.Of<IPluginHostServices>());
        var request = Request();
        var result = await plugin.RescoreAsync(request, CancellationToken.None);
        Assert.Equal(request.RecordingId, result.RecordingId);
        Assert.Empty(result.Replacements);
        Assert.Equal("TypeWhisper", Assert.Single(request.Terms).Text);
        Assert.Equal("Type whisper", request.Text);
        Assert.Equal(16000, request.Audio.Length);
    }

    [Fact]
    public async Task CancelledRequestDoesNotProduceResult()
    {
        using var plugin = new ContractProbePlugin();
        await plugin.ActivateAsync(Mock.Of<IPluginHostServices>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.RescoreAsync(Request(), new CancellationToken(true)));
    }

    [Fact]
    public async Task InactiveAndDisposedPluginRejectRequests()
    {
        var plugin = new ContractProbePlugin();
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.RescoreAsync(Request(), CancellationToken.None));
        plugin.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.ActivateAsync(Mock.Of<IPluginHostServices>()));
    }

    [Fact]
    public async Task UnsupportedAudioHasExplicitError()
    {
        using var plugin = new ContractProbePlugin();
        await plugin.ActivateAsync(Mock.Of<IPluginHostServices>());
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RescoreAsync(Request() with { SampleRate = 48000 }, CancellationToken.None));
    }
}
