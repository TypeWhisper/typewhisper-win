using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationRecoveryControllerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tw-recovery-review-" + Guid.NewGuid().ToString("N"));
    private DictationRecoveryAudioStore _store = null!;
    public async Task InitializeAsync() { _store = new(_root); await _store.InitializeAsync(); }
    public async Task DisposeAsync() { await _store.DisposeAsync(); Directory.Delete(_root, true); }
    private async Task<RecoveryRecordingDescriptor> Seed()
    {
        var id = Assert.IsType<Guid>(_store.BeginRecording()); _store.AppendSamples(id, [0.1f, 0.2f]);
        var lease = Assert.IsType<RecoveryRecordingLease>(await _store.FinalizeRecordingAsync(id));
        return Assert.IsType<RecoveryRecordingDescriptor>(await lease.PreserveAsync());
    }

    [Fact]
    public async Task ReentrantShutdownFromChangedPreventsDecoderStart()
    {
        var recording = await Seed(); var calls = 0;
        var controller = new DictationRecoveryController(_store, (_, _) => { calls++; return Task.FromResult("unexpected"); });
        controller.Changed += () => { if (controller.Busy) _ = controller.ShutdownAsync(); };
        await controller.RetryAsync(recording.Id);
        Assert.Equal(0, calls); Assert.Null(controller.Review); Assert.Single(controller.Recordings);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotEscapeOrReleaseDecoderEarly()
    {
        var recording = await Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new DictationRecoveryController(_store, async (_, token) =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("callback"));
            entered.SetResult(); await release.Task; return "late";
        });
        var run = controller.RetryAsync(recording.Id); await entered.Task;
        var drain = controller.ShutdownAsync(); Assert.False(drain.IsCompleted);
        release.SetResult(); await Task.WhenAll(run, drain);
        Assert.Null(controller.Review); Assert.Single(controller.Recordings);
    }

    [Fact]
    public async Task RefreshDoesNotTranscribeAndRetryKeepsOriginalAudio()
    {
        var recording = await Seed(); var path = _store.GetRecordingPath(recording.Id)!; var before = File.ReadAllBytes(path);
        var calls = 0;
        var controller = new DictationRecoveryController(_store, (source, _) =>
        { Assert.Equal(path, source); calls++; return Task.FromResult("Recovered text"); });
        await controller.RefreshAsync(); Assert.Equal(0, calls); Assert.Null(controller.Review);
        await controller.RetryAsync(recording.Id);
        Assert.Equal(1, calls); Assert.Equal("Recovered text", controller.Review!.Text);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Single(controller.Recordings);
        await controller.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownWaitsForLateDecoderAndRejectsItsResultOrError(bool fail)
    {
        var recording = await Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var controller = new DictationRecoveryController(_store, async (_, _) =>
        { calls++; entered.SetResult(); await release.Task; if (fail) throw new IOException(); return "Late text"; });
        var run = controller.RetryAsync(recording.Id); await entered.Task;
        var drain = controller.ShutdownAsync(); Assert.False(drain.IsCompleted);
        Assert.Same(drain, controller.ShutdownAsync());
        await controller.RetryAsync(recording.Id); Assert.Equal(1, calls);
        release.SetResult(); await Task.WhenAll(run, drain);
        Assert.Null(controller.Review); Assert.Single(controller.Recordings); Assert.False(controller.Busy);
        Assert.True(File.Exists(_store.GetRecordingPath(recording.Id)));
    }

    [Fact]
    public async Task CanceledRetryKeepsPreviousReviewAndCanBeRetriedAfterDrain()
    {
        var recording = await Seed(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new DictationRecoveryController(_store, async (_, _) =>
        { if (++calls == 2) { entered.SetResult(); await release.Task; } return "Text " + calls; });
        await controller.RetryAsync(recording.Id); var first = controller.Review;
        var run = controller.RetryAsync(recording.Id); await entered.Task; var drain = controller.CancelAndDrainAsync();
        Assert.False(drain.IsCompleted); Assert.False(controller.IsShutdown);
        await controller.RetryAsync(recording.Id); Assert.Equal(2, calls);
        release.SetResult(); await Task.WhenAll(run, drain); Assert.Same(first, controller.Review);
        await controller.RetryAsync(recording.Id); Assert.Equal("Text 3", controller.Review!.Text);
        Assert.Single(controller.Recordings); await controller.ShutdownAsync();
    }

    [Fact]
    public async Task FailedRetryAndDeletionOfMissingIdKeepAudioAndReview()
    {
        var recording = await Seed(); var fail = false;
        var controller = new DictationRecoveryController(_store, (_, _) => fail ? Task.FromException<string>(new IOException()) : Task.FromResult("Kept text"));
        await controller.RetryAsync(recording.Id); var review = controller.Review; fail = true;
        await controller.RetryAsync(recording.Id); Assert.Same(review, controller.Review);
        await controller.DeleteConfirmedAsync("../outside.wav"); Assert.Single(controller.Recordings);
        Assert.Same(review, controller.Review); await controller.ShutdownAsync();
    }

    [Fact]
    public async Task ConfirmedDeletionTargetsOnlySnapshotIdAndKeepsReview()
    {
        var first = await Seed(); var controller = new DictationRecoveryController(_store, (_, _) => Task.FromResult("Review"));
        await controller.RetryAsync(first.Id); var later = await Seed();
        await controller.DeleteConfirmedAsync(first.Id);
        Assert.Equal(later.Id, Assert.Single(controller.Recordings).Id);
        Assert.Equal("Review", controller.Review!.Text); await controller.ShutdownAsync();
    }
}
