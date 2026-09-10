using System.Text.Json;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SpokenFeedbackTests
{
    [Fact]
    public void OldAudioPreferencesRemainOptedOutAndVoiceRoundTrips()
    {
        var old = JsonSerializer.Deserialize<DictationAudioPreferences>("{\"SoundFeedbackEnabled\":true}")!;
        Assert.False(old.SpokenFeedbackEnabled);
        Assert.Null(old.SpokenFeedbackVoiceId);
        var selected = old with { SpokenFeedbackEnabled = true, SpokenFeedbackVoiceId = "installed-voice", OutputDeviceId = "endpoint" };
        Assert.Equal(selected, JsonSerializer.Deserialize<DictationAudioPreferences>(JsonSerializer.Serialize(selected)));
        Assert.Null((selected with { SpokenFeedbackVoiceId = " " }).Validated().SpokenFeedbackVoiceId);
    }

    [Theory]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, true, false, true)]
    public void AutomaticAdmissionRequiresSuccessfulFinalOutput(bool enabled, bool processing,
        bool delivered, bool review, bool expected) =>
        Assert.Equal(expected, SpokenFeedbackPolicy.ShouldSpeakAutomatically(enabled, processing, delivered, review));

    [Fact]
    public async Task InvalidTextNeverReachesBackendAndBoundaryIsNotTruncated()
    {
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        Assert.Equal(SpokenFeedbackStatus.Rejected, (await controller.SpeakAsync(new(" "))).Status);
        Assert.Equal(SpokenFeedbackStatus.Rejected,
            (await controller.SpeakAsync(new(new string('a', SpokenFeedbackRequest.MaxTextLength + 1)))).Status);
        Assert.Empty(backend.Requests);
        var request = new SpokenFeedbackRequest(new string('a', SpokenFeedbackRequest.MaxTextLength), "de", "voice", "device");
        Assert.Equal(SpokenFeedbackStatus.Completed, (await controller.SpeakAsync(request)).Status);
        Assert.Same(request, Assert.Single(backend.Requests));
    }

    [Fact]
    public async Task OverlapIsRejectedAndCancellationWaitsForUncooperativeBackend()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { Run = (_, _) => release.Task };
        var controller = new SpokenFeedbackController(backend);
        var playing = controller.SpeakAsync(new("first"));
        Assert.True(controller.IsBusy);
        Assert.Equal(SpokenFeedbackStatus.Rejected, (await controller.SpeakAsync(new("second"))).Status);
        var drain = controller.CancelAndDrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.True(backend.Token.IsCancellationRequested);
        Assert.Same(drain, controller.CancelAndDrainAsync());
        Assert.Equal(SpokenFeedbackStatus.Rejected, (await controller.SpeakAsync(new("while stopping"))).Status);
        release.SetResult();
        await drain;
        Assert.Equal(SpokenFeedbackStatus.Canceled, (await playing).Status);
        Assert.False(controller.IsBusy);
        Assert.False(controller.IsShutdown);
        Assert.Equal(SpokenFeedbackStatus.Completed, (await controller.SpeakAsync(new("retry explicitly"))).Status);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task ShutdownDrainsLateErrorAndPermanentlyRejectsRequests()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { Run = (_, _) => release.Task };
        var controller = new SpokenFeedbackController(backend);
        var playing = controller.SpeakAsync(new("private text"));
        var shutdown = controller.ShutdownAsync();
        Assert.True(controller.IsShutdown);
        Assert.False(shutdown.IsCompleted);
        Assert.Same(shutdown, controller.ShutdownAsync());
        release.SetException(new IOException("backend detail containing private text"));
        await shutdown;
        var result = await playing;
        Assert.Equal(SpokenFeedbackStatus.Canceled, result.Status);
        Assert.DoesNotContain("private text", result.Message!);
        Assert.Equal(SpokenFeedbackStatus.Rejected, (await controller.SpeakAsync(new("after shutdown"))).Status);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task FailureDoesNotRetryOrExposeBackendDetails()
    {
        var backend = new Backend { Run = (_, _) => throw new InvalidOperationException("secret text") };
        var controller = new SpokenFeedbackController(backend);
        var result = await controller.SpeakAsync(new("transcript"));
        Assert.Equal(SpokenFeedbackStatus.Failed, result.Status);
        Assert.DoesNotContain("secret", result.Message!);
        Assert.Single(backend.Requests);
        Assert.False(controller.IsBusy);
        await controller.CancelAndDrainAsync();
    }

    [Fact]
    public async Task CancellationCallbackMayReenterControllerAndThrowWithoutBypassingDrain()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend();
        var controller = new SpokenFeedbackController(backend);
        Task<SpokenFeedbackResult>? reentrant = null;
        backend.Run = async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                reentrant = controller.SpeakAsync(new("reentrant"));
                throw new InvalidOperationException("callback failure");
            });
            await release.Task;
        };
        var playing = controller.SpeakAsync(new("first"));
        var drain = controller.CancelAndDrainAsync();
        Assert.NotNull(reentrant);
        Assert.Equal(SpokenFeedbackStatus.Rejected, (await reentrant).Status);
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await drain;
        Assert.Equal(SpokenFeedbackStatus.Canceled, (await playing).Status);
        Assert.Single(backend.Requests);
    }

    private sealed class Backend : ISpokenFeedbackBackend
    {
        public List<SpokenFeedbackRequest> Requests { get; } = [];
        public CancellationToken Token { get; private set; }
        public Func<SpokenFeedbackRequest, CancellationToken, Task> Run { get; set; } = (_, _) => Task.CompletedTask;
        public IReadOnlyList<SpokenFeedbackVoice> GetVoices() => [];
        public Task SpeakAsync(SpokenFeedbackRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Token = cancellationToken;
            return Run(request, cancellationToken);
        }
    }
}
