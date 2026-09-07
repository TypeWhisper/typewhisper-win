using System.Buffers.Binary;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class RecorderControllerTests
{
    private sealed class Reservation : IDisposable
    {
        public int Releases;
        public void Dispose() => Releases++;
    }

    [Fact]
    public async Task FailedSecondSourceRollsBackBothAttemptedSources()
    {
        var stopped = new List<string>();
        var sources = new RecorderSourceCoordinator(() => Task.CompletedTask,
            () => throw new IOException("system start failed"),
            () => { stopped.Add("mic"); return Task.FromResult(new[] { 0.1f }); },
            () => { stopped.Add("system"); return Task.FromResult(Array.Empty<float>()); });
        await Assert.ThrowsAsync<IOException>(() => sources.StartAsync(true, true));
        Assert.Equal(new[] { "mic", "system" }, stopped);
        await sources.StopAsync();
        Assert.Equal(2, stopped.Count);
    }

    [Fact]
    public async Task FailedMicrophoneStopStillStopsSystemAndRetainsItsAudio()
    {
        float[] samples = [0.2f, -0.2f];
        var sources = new RecorderSourceCoordinator(() => Task.CompletedTask, () => Task.CompletedTask,
            () => throw new IOException("stop failed"), () => Task.FromResult(samples));
        await sources.StartAsync(true, true);
        var result = await sources.StopAsync();
        Assert.Same(samples, result.System);
        Assert.Single(result.Warnings);
        Assert.Empty(result.Microphone);
    }

    [Fact]
    public async Task SaveFailureRetainsExactSamplesForRetryWithoutAnotherCapture()
    {
        var reservation = new Reservation();
        float[] samples = [0.12345f, -0.23f];
        var stops = 0; var saves = 0;
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => { stops++; return Task.FromResult(samples); }, audio =>
            {
                Assert.Same(samples, audio);
                return ++saves == 1 ? Task.FromException<string>(new IOException("disk full")) : Task.FromResult("saved.wav");
            });
        await recorder.StartAsync(true, false);
        await Assert.ThrowsAsync<IOException>(recorder.StopAndSaveAsync);
        Assert.Equal(RecorderState.SaveFailed, recorder.State);
        Assert.Equal(1, reservation.Releases);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(true, false));
        await recorder.RetrySaveAsync();
        Assert.Equal(RecorderState.Saved, recorder.State);
        Assert.Equal("saved.wav", recorder.FilePath);
        Assert.Equal(1, stops);
        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task ReservationIsHeldUntilSaveCompletesAndShutdownRejectsNewStarts()
    {
        var reservation = new Reservation();
        var save = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => Task.FromResult(new[] { 0.1f }), _ => save.Task);
        await recorder.StartAsync(true, true);
        var shutdown = recorder.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, reservation.Releases);
        save.SetResult("saved.wav");
        await shutdown;
        Assert.Equal(1, reservation.Releases);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(true, true));
    }

    [Fact]
    public async Task SixtyMinuteLimitStopsAndSavesOnce()
    {
        var stops = 0;
        var recorder = new RecorderController(() => new Reservation(), (_, _) => Task.CompletedTask,
            () => { stops++; return Task.FromResult(new float[160]); }, _ => Task.FromResult("saved.wav"));
        await recorder.StartAsync(false, true);
        await recorder.StopAtLimitAsync(TimeSpan.FromMinutes(59.9));
        Assert.Equal(0, stops);
        await recorder.StopAtLimitAsync(TimeSpan.FromMinutes(60));
        await recorder.StopAtLimitAsync(TimeSpan.FromMinutes(61));
        Assert.Equal(1, stops);
        Assert.Equal(TimeSpan.FromMilliseconds(10), recorder.Duration);
    }

    [Fact]
    public async Task ReservationFailureNeverStartsAudio()
    {
        var recorder = new RecorderController(() => throw new InvalidOperationException("dictation busy"),
            (_, _) => throw new Exception("Must not capture"), () => Task.FromResult(Array.Empty<float>()), _ => Task.FromResult(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(true, false));
        Assert.False(recorder.Busy);
        Assert.Equal(RecorderState.Ready, recorder.State);
    }

    [Fact]
    public async Task FailedShutdownSaveCanBeRetriedWithoutLosingAudioOrKeepingCaptureReservation()
    {
        var reservation = new Reservation();
        var fail = true;
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => Task.FromResult(new[] { 0.25f }), _ => fail ? Task.FromException<string>(new IOException("disk unavailable")) : Task.FromResult("saved.wav"));
        await recorder.StartAsync(true, false);
        await Assert.ThrowsAsync<IOException>(recorder.ShutdownAsync);
        Assert.Equal(RecorderState.SaveFailed, recorder.State);
        Assert.Equal(1, reservation.Releases);
        fail = false;
        await recorder.RetrySaveAsync();
        await recorder.ShutdownAsync();
        Assert.Equal("saved.wav", recorder.FilePath);
        Assert.Equal(1, reservation.Releases);
    }

    [Fact]
    public async Task DuplicateStopSharesPendingSaveAndDoesNotStopSourcesAgain()
    {
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        var recorder = new RecorderController(() => new Reservation(), (_, _) => Task.CompletedTask,
            () => { stops++; return Task.FromResult(new[] { 0.1f }); }, _ => saved.Task);
        await recorder.StartAsync(true, false);
        var pending = recorder.StopAndSaveAsync();
        Assert.Same(pending, recorder.StopAndSaveAsync());
        saved.SetResult("saved.wav");
        await pending;
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task NativeCleanupFailureKeepsReservationUntilSuccessfulStopRetry()
    {
        var reservation = new Reservation();
        var fail = true;
        var sources = new RecorderSourceCoordinator(() => Task.CompletedTask, () => Task.CompletedTask,
            () => Task.FromResult(new[] { 0.1f }), () => fail
                ? Task.FromException<float[]>(new RecorderCleanupException("still owned", new IOException())) : Task.FromResult(new[] { 0.2f }));
        var recorder = new RecorderController(() => reservation, sources.StartAsync,
            async () => (await sources.StopAsync()).Microphone, _ => Task.FromResult("saved.wav"));
        await recorder.StartAsync(true, true);
        await Assert.ThrowsAsync<RecorderCleanupException>(recorder.StopAndSaveAsync);
        Assert.Equal(0, reservation.Releases);
        Assert.Equal(RecorderState.Recording, recorder.State);
        fail = false;
        await recorder.StopAndSaveAsync();
        Assert.Equal(1, reservation.Releases);
        Assert.Equal(RecorderState.Saved, recorder.State);
    }

    [Fact]
    public async Task ThrowingFinalListenerCannotLeaveTransitionCompletionPending()
    {
        var recorder = new RecorderController(() => new Reservation(), (_, _) => Task.CompletedTask,
            () => Task.FromResult(new[] { 0.1f }), _ => Task.FromResult("saved.wav"));
        var notifications = 0;
        recorder.Changed += () => { if (!recorder.Busy) throw new IOException("listener failed"); };
        recorder.Changed += () => notifications++;
        var pending = recorder.StartAsync(true, false);
        Assert.True(pending.IsCompleted);
        await pending;
        Assert.False(recorder.Busy);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task ThrowingSavingListenerCannotPreventSourceStopOrShutdownDrain()
    {
        var reservation = new Reservation();
        var stopped = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => { stops++; return stopped.Task; }, _ => Task.FromResult("saved.wav"));
        await recorder.StartAsync(true, false);
        recorder.Changed += () => { if (recorder.State == RecorderState.Saving) throw new IOException("listener failed"); };
        var shutdown = recorder.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(1, stops);
        Assert.Equal(0, reservation.Releases);
        stopped.SetResult([0.25f]);
        await shutdown;
        Assert.Equal(RecorderState.Saved, recorder.State);
        Assert.Equal(1, reservation.Releases);
        Assert.False(recorder.Busy);
    }

    [Fact]
    public async Task ShutdownOfCleanlyStoppedEmptyCaptureCompletesWithoutPublishingFile()
    {
        var reservation = new Reservation();
        var stops = 0;
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => { stops++; return Task.FromResult(Array.Empty<float>()); },
            _ => throw new InvalidOperationException("Empty capture must not be published."));
        await recorder.StartAsync(true, false);
        await recorder.ShutdownAsync();
        await recorder.ShutdownAsync();
        Assert.Equal(1, stops);
        Assert.Equal(1, reservation.Releases);
        Assert.Equal(RecorderState.Ready, recorder.State);
        Assert.Null(recorder.FilePath);
        Assert.False(recorder.Busy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(true, false));
    }

    [Fact]
    public async Task WavPublicationHasRealSampleRateChannelsDurationAndNoTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "recorder-test-" + Guid.NewGuid());
        try
        {
            var path = await RecorderWavStore.SaveAsync(directory, Enumerable.Repeat(0.25f, 3200).ToArray());
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
            Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
            Assert.Equal(6400, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
            Assert.Equal(6444, bytes.Length);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("Grüße aus Köln", "Grüße aus Köln")]
    [InlineData("../../CON:<test>|?*\\audio", "_.._CON__test_____audio")]
    [InlineData("   ", "")]
    [InlineData("CON", "CON")]
    public async Task RecordingTitleIsPersistedInSafeFilename(string title, string expectedTitle)
    {
        var directory = Path.Combine(Path.GetTempPath(), "recorder-title-" + Guid.NewGuid());
        try
        {
            Assert.Equal(expectedTitle, RecorderWavStore.NormalizeTitle(title));
            var path = await RecorderWavStore.SaveAsync(directory, [0.25f], title);
            Assert.Equal(Path.GetFullPath(directory), Path.GetDirectoryName(Path.GetFullPath(path)));
            var name = Path.GetFileName(path);
            Assert.StartsWith(expectedTitle.Length == 0 ? "recording-" : $"recording-{expectedTitle}-", name);
            Assert.DoesNotContain(name, c => c < 32 || "<>:\"/\\|?*".Contains(c));
            Assert.True(File.Exists(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RetryPublishesFrozenBoundedTitleAfterFailedWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "recorder-title-" + Guid.NewGuid());
        var frozenTitle = RecorderWavStore.NormalizeTitle(new string('ü', 100));
        Assert.Equal(64, frozenTitle.Length);
        try
        {
            await File.WriteAllTextAsync(directory, "block directory creation");
            var recorder = new RecorderController(() => new Reservation(), (_, _) => Task.CompletedTask,
                () => Task.FromResult(new[] { 0.25f }), audio => RecorderWavStore.SaveAsync(directory, audio, frozenTitle));
            await recorder.StartAsync(true, false);
            var failure = await Record.ExceptionAsync(recorder.StopAndSaveAsync);
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(RecorderState.SaveFailed, recorder.State);
            File.Delete(directory);
            await recorder.RetrySaveAsync();
            Assert.StartsWith($"recording-{frozenTitle}-", Path.GetFileName(recorder.FilePath!));
            Assert.True(File.Exists(recorder.FilePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            else if (File.Exists(directory)) File.Delete(directory);
        }
    }
}
