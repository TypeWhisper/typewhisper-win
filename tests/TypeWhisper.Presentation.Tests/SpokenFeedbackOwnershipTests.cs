using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SpokenFeedbackOwnershipTests
{
    [Fact]
    public async Task MatchingHistoryRequestCancelsAndWaitsForBackendDrain()
    {
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        var request = new SpokenFeedbackRequest("Saved history text", "de", "voice", "output");
        var playback = controller.SpeakAsync(request);
        var stop = controller.CancelAndDrainAsync(request);
        Assert.Same(request, backend.Request);
        Assert.True(backend.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        backend.Completion.SetResult();
        await stop;
        Assert.Equal(SpokenFeedbackStatus.Canceled, (await playback).Status);
    }

    [Fact]
    public async Task EqualButDifferentRequestCannotCancelCurrentSpeech()
    {
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        var request = new SpokenFeedbackRequest("Same text");
        var playback = controller.SpeakAsync(request);
        await controller.CancelAndDrainAsync(request with { });
        Assert.False(backend.Token.IsCancellationRequested);
        Assert.True(controller.IsBusy);
        backend.Completion.SetResult();
        Assert.Equal(SpokenFeedbackStatus.Completed, (await playback).Status);
    }

    [Fact]
    public async Task LateHistoryCleanupDoesNotCancelNewAutomaticOrShortcutSpeech()
    {
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        var history = new SpokenFeedbackRequest("History");
        var first = controller.SpeakAsync(history);
        backend.Completion.SetResult();
        await first;
        backend.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = controller.SpeakAsync(new("New dictation"));
        await controller.CancelAndDrainAsync(history);
        Assert.False(backend.Token.IsCancellationRequested);
        Assert.False(next.IsCompleted);
        backend.Completion.SetResult();
        Assert.Equal(SpokenFeedbackStatus.Completed, (await next).Status);
    }

    private sealed class Backend : ISpokenFeedbackBackend
    {
        internal CancellationToken Token;
        internal SpokenFeedbackRequest? Request;
        internal TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<SpokenFeedbackVoice> GetVoices() => [];
        public Task SpeakAsync(SpokenFeedbackRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Token = cancellationToken;
            return Completion.Task;
        }
    }
}
