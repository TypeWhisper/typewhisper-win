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

    [Fact]
    public async Task PreviewStreamRunsWithoutThePromptAndLeavesTheFinalTextToTheCompleteRecording()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsStreaming).Returns(true);
        engine.SetupGet(e => e.SupportsStreamingCompletion).Returns(true);
        engine.SetupGet(e => e.SupportsDictionaryTerms).Returns(true);
        engine.SetupGet(e => e.DictionaryTermsBudget).Returns(DictionaryTermsBudget.Default);
        engine.Setup(e => e.SupportsStreamingForPrompt("TypeWhisper")).Returns(false);
        engine.Setup(e => e.SupportsStreamingPreviewForPrompt("TypeWhisper")).Returns(true);
        var session = new Session();
        engine.Setup(e => e.StartStreamingWithLanguageHintsAndPromptAsync(
            It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var failed = false;
        var published = new List<string>();
        var firstAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Sent += () => firstAudio.TrySetResult();
        await using var stream = new StreamingDictation((use, ct) => use(engine.Object, ct),
            ["de"], text => { lock (published) published.Add(text); }, () => failed = true, default, ["TypeWhisper"]);
        stream.Append(new float[160]);
        await firstAudio.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Raise("Live text", isFinal: true);
        Assert.True(stream.IsPreviewOnly);
        Assert.Null(await stream.FinishAsync(160).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(failed);
        Assert.Contains("Live text", published);
        Assert.Equal(0, session.Finalized);
        Assert.True(session.Disposed);
        engine.Verify(e => e.StartStreamingWithLanguageHintsAndPromptAsync(
            It.IsAny<IReadOnlyList<string>>(), "TypeWhisper", It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ProviderWithoutPreviewSupportKeepsRejectingAPromptItCannotStream()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsStreaming).Returns(true);
        engine.SetupGet(e => e.SupportsStreamingCompletion).Returns(true);
        engine.SetupGet(e => e.SupportsDictionaryTerms).Returns(true);
        engine.SetupGet(e => e.DictionaryTermsBudget).Returns(DictionaryTermsBudget.Default);
        engine.Setup(e => e.SupportsStreamingForPrompt("TypeWhisper")).Returns(false);
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine.Object, ct),
            ["de"], _ => { }, () => failed = true, default, ["TypeWhisper"]);
        Assert.Null(await stream.FinishAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(failed);
        Assert.False(stream.IsPreviewOnly);
        engine.Verify(e => e.StartStreamingWithLanguageHintsAndPromptAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private sealed class Session : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;
        public event Action? Sent;
        public bool Disposed;
        public int Finalized;
        public void Raise(string text, bool isFinal) => TranscriptReceived?.Invoke(new(text, isFinal));
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) { Sent?.Invoke(); return Task.CompletedTask; }
        public Task FinalizeAsync(CancellationToken ct)
        {
            Finalized++;
            TranscriptReceived?.Invoke(new("Complete.", true));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
