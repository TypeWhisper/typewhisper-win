using System.Buffers.Binary;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.Presentation;

public sealed class BufferedStreamingStartupTests
{
    [Fact]
    public async Task DelayedProviderSetupAndConnectionPreserveEverySampleEvenWhenStoppedBeforeConnecting()
    {
        var buffer = new BufferedAudioHandoff();
        buffer.Begin();
        buffer.Append([0.125f, 0.25f]); // Audio arrives before dictionary/provider setup finishes.
        var connected = new TaskCompletionSource<IStreamingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsStreaming).Returns(true);
        engine.SetupGet(e => e.SupportsStreamingCompletion).Returns(true);
        engine.Setup(e => e.SupportsStreamingForPrompt(It.IsAny<string?>())).Returns(true);
        engine.Setup(e => e.StartStreamingWithLanguageHintsAndPromptAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(connected.Task);
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine.Object, ct), ["de"], _ => { }, () => failed = true, default);
        Assert.True(buffer.Attach(samples => stream.Append(samples)));
        buffer.Append([0.375f, 0.5f]); // Connection is still pending.
        var finish = stream.FinishAsync(expectedSamples: 4);
        Assert.False(finish.IsCompleted);
        var session = new Session();
        connected.SetResult(session);
        Assert.Equal("First words preserved.", await finish.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(failed);
        Assert.Equal(new short[] { 4096, 8192, 12288, 16384 }, session.Samples);
        buffer.Reset();
    }

    private sealed class Session : IStreamingSession
    {
        public readonly List<short> Samples = [];
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        {
            for (var offset = 0; offset < audio.Length; offset += 2)
                Samples.Add(BinaryPrimitives.ReadInt16LittleEndian(audio.Span[offset..]));
            return Task.CompletedTask;
        }
        public Task FinalizeAsync(CancellationToken ct)
        {
            TranscriptReceived?.Invoke(new("First words preserved.", true));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
