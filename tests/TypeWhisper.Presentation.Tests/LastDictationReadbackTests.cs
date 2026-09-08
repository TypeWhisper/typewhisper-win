using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LastDictationReadbackTests
{
    private static LastCompletedDictation Snapshot(string text = "Fertiger Text.") =>
        new("id", text, "de", "engine", "model", DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task RoutesExactFinalTextLanguageVoiceAndOutput()
    {
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        Assert.Equal(SpokenFeedbackStatus.Completed,
            (await LastDictationReadback.ToggleAsync(controller, Snapshot(), "voice", "output")).Status);
        Assert.Equal(new SpokenFeedbackRequest("Fertiger Text.", "de", "voice", "output"), Assert.Single(backend.Requests));
    }

    [Fact]
    public async Task EmptySessionDoesNotInvokeSpeech()
    {
        var backend = new Backend();
        Assert.Equal(SpokenFeedbackStatus.Rejected,
            (await LastDictationReadback.ToggleAsync(new(backend), null, null, null)).Status);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task OversizedTextIsRejectedWithoutTruncationOrBackendCall()
    {
        var backend = new Backend();
        Assert.Equal(SpokenFeedbackStatus.Rejected,
            (await LastDictationReadback.ToggleAsync(new(backend), Snapshot(new string('a', SpokenFeedbackRequest.MaxTextLength + 1)), null, null)).Status);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task SecondInvocationStopsAndWaitsForDrainWithoutStartingAnotherRequest()
    {
        var backend = new Backend { Wait = true };
        var controller = new SpokenFeedbackController(backend);
        var first = LastDictationReadback.ToggleAsync(controller, Snapshot(), null, null);
        var stop = LastDictationReadback.ToggleAsync(controller, Snapshot("must not play"), null, null);
        Assert.True(backend.Cancellation.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        Assert.True(controller.IsBusy);
        backend.Drained.SetResult();
        Assert.Equal(SpokenFeedbackStatus.Canceled, (await stop).Status);
        Assert.Equal(SpokenFeedbackStatus.Canceled, (await first).Status);
        Assert.Single(backend.Requests);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task ShutdownPreventsNewReadback()
    {
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        await controller.ShutdownAsync();
        Assert.Equal(SpokenFeedbackStatus.Rejected,
            (await LastDictationReadback.ToggleAsync(controller, Snapshot(), null, null)).Status);
        Assert.Empty(backend.Requests);
    }

    private sealed class Backend : ISpokenFeedbackBackend
    {
        internal bool Wait;
        internal List<SpokenFeedbackRequest> Requests = [];
        internal CancellationToken Cancellation;
        internal TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<SpokenFeedbackVoice> GetVoices() => [];
        public Task SpeakAsync(SpokenFeedbackRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Cancellation = cancellationToken;
            return Wait ? Drained.Task : Task.CompletedTask;
        }
    }
}
