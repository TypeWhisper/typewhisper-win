using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class StreamingDictionaryRoutingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClippedDictionaryPromptControlsRoutingAndReachesTheLiveSession(bool acceptsPrompt)
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsStreaming).Returns(true);
        engine.SetupGet(e => e.SupportsStreamingCompletion).Returns(true);
        engine.SetupGet(e => e.SupportsDictionaryTerms).Returns(true);
        engine.SetupGet(e => e.DictionaryTermsBudget).Returns(new DictionaryTermsBudget(MaxTotalChars: 6));
        engine.Setup(e => e.SupportsStreamingForPrompt("AA, BB")).Returns(acceptsPrompt);
        var session = new Session();
        engine.Setup(e => e.StartStreamingWithLanguageHintsAndPromptAsync(
            It.IsAny<IReadOnlyList<string>>(), "AA, BB", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine.Object, ct),
            ["de"], _ => { }, () => failed = true, default, ["AA", "BB", "CC"]);
        stream.Append(new float[160]);
        Assert.Equal(acceptsPrompt ? "Complete." : null, await stream.FinishAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(!acceptsPrompt, failed);
        engine.Verify(e => e.SupportsStreamingForPrompt("AA, BB"), Times.Once());
        engine.Verify(e => e.StartStreamingWithLanguageHintsAndPromptAsync(
            It.IsAny<IReadOnlyList<string>>(), "AA, BB", It.IsAny<CancellationToken>()),
            acceptsPrompt ? Times.Once() : Times.Never());
        Assert.Equal(acceptsPrompt, session.Disposed);
    }

    private sealed class Session : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;
        public bool Disposed;
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizeAsync(CancellationToken ct)
        {
            TranscriptReceived?.Invoke(new("Complete.", true));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
