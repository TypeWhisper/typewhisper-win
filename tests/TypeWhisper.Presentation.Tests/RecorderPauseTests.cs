using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class RecorderPauseTests
{
    private sealed class Reservation : IDisposable
    {
        internal int Released;
        public void Dispose() => Released++;
    }

    [Fact]
    public async Task PauseResumeConcatenatesActiveSegmentsAndKeepsFrozenSourcesAndReservation()
    {
        var reservation = new Reservation();
        var starts = new List<(bool, bool)>();
        var segments = new Queue<float[]>([[0, 0.2f, 0], [0, 0.4f, 0]]);
        float[]? output = null;
        var recorder = new RecorderController(() => reservation, (mic, system) =>
        { starts.Add((mic, system)); return Task.CompletedTask; }, () => Task.FromResult(segments.Dequeue()),
            audio => { output = audio; return Task.FromResult("recording.wav"); });
        await recorder.StartAsync(true, true);
        await recorder.PauseAsync();
        Assert.Equal(RecorderState.Paused, recorder.State); Assert.Equal(0, reservation.Released);
        Assert.Equal(TimeSpan.FromSeconds(3.0 / 16000), recorder.Duration);
        await recorder.ResumeAsync(); await recorder.StopAndSaveAsync();
        Assert.Equal(new[] { (true, true), (true, true) }, starts);
        Assert.Equal(new float[] { 0, 0.2f, 0, 0, 0.4f, 0 }, output);
        Assert.Equal(1, reservation.Released);
    }

    [Fact]
    public async Task ResumeFailureRetainsPausedAudioAndShutdownSavesWithoutStoppingAgain()
    {
        var reservation = new Reservation(); var starts = 0; var stops = 0;
        var recorder = new RecorderController(() => reservation, (_, _) => ++starts == 1
            ? Task.CompletedTask : Task.FromException(new IOException("device unavailable")),
            () => { stops++; return Task.FromResult(new[] { 0.25f }); },
            audio => { Assert.Equal(new[] { 0.25f }, audio); return Task.FromResult("retained.wav"); });
        await recorder.StartAsync(false, true); await recorder.PauseAsync();
        await Assert.ThrowsAsync<IOException>(recorder.ResumeAsync);
        Assert.Equal(RecorderState.Paused, recorder.State); Assert.Equal(0, reservation.Released);
        await recorder.ShutdownAsync();
        Assert.Equal(1, stops); Assert.Equal(1, reservation.Released);
        Assert.Equal(RecorderState.Saved, recorder.State);
    }

    [Fact]
    public async Task PauseCleanupFailureNeverClaimsPausedAndRetryAddsSegmentOnlyOnce()
    {
        var reservation = new Reservation(); var stops = 0;
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => ++stops == 1 ? Task.FromException<float[]>(new RecorderCleanupException("retry stop", new IOException()))
                : Task.FromResult(new[] { 0.5f }),
            audio => { Assert.Single(audio); return Task.FromResult("one.wav"); });
        await recorder.StartAsync(true, false);
        await Assert.ThrowsAsync<RecorderCleanupException>(recorder.PauseAsync);
        Assert.Equal(RecorderState.Recording, recorder.State); Assert.Equal(0, reservation.Released);
        await recorder.PauseAsync(); await recorder.StopAndSaveAsync();
        Assert.Equal(2, stops); Assert.Equal(1, reservation.Released);
    }

    [Fact]
    public async Task ShutdownDuringPauseDrainsSourceThenSavesAndCannotResume()
    {
        var stopped = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = new Reservation(); var stops = 0;
        var recorder = new RecorderController(() => reservation, (_, _) => Task.CompletedTask,
            () => { stops++; return stopped.Task; }, _ => Task.FromResult("saved.wav"));
        await recorder.StartAsync(true, true);
        var pause = recorder.PauseAsync(); var shutdown = recorder.ShutdownAsync();
        Assert.False(shutdown.IsCompleted); Assert.Equal(0, reservation.Released);
        stopped.SetResult([0.3f]); await pause; await shutdown;
        Assert.Equal(1, stops); Assert.Equal(RecorderState.Saved, recorder.State);
        await recorder.ResumeAsync(); Assert.Equal(RecorderState.Saved, recorder.State);
    }

    [Fact]
    public async Task SaveRetryReusesWholeRecordingWithoutRepeatingSources()
    {
        var source = new Queue<float[]>([[1], [2]]); var saves = 0;
        var recorder = new RecorderController(() => new Reservation(), (_, _) => Task.CompletedTask,
            () => Task.FromResult(source.Dequeue()), audio =>
            {
                Assert.Equal(new float[] { 1, 2 }, audio);
                return ++saves == 1 ? Task.FromException<string>(new IOException()) : Task.FromResult("saved.wav");
            });
        await recorder.StartAsync(true, false); await recorder.PauseAsync(); await recorder.ResumeAsync();
        await Assert.ThrowsAsync<IOException>(recorder.StopAndSaveAsync);
        await recorder.RetrySaveAsync(); Assert.Empty(source); Assert.Equal(2, saves);
    }

    [Fact]
    public async Task ShutdownDuringResumeWaitsForStartAndStopsTheNewSegment()
    {
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = new Reservation(); var starts = 0; var stops = 0;
        var recorder = new RecorderController(() => reservation,
            (_, _) => ++starts == 1 ? Task.CompletedTask : resumed.Task,
            () => Task.FromResult(new[] { (float)++stops }),
            audio => { Assert.Equal(new float[] { 1, 2 }, audio); return Task.FromResult("resumed.wav"); });
        await recorder.StartAsync(true, true); await recorder.PauseAsync();
        var resume = recorder.ResumeAsync(); var shutdown = recorder.ShutdownAsync();
        Assert.False(shutdown.IsCompleted); Assert.Equal(0, reservation.Released);
        resumed.SetResult(); await resume; await shutdown;
        Assert.Equal(2, stops); Assert.Equal(1, reservation.Released);
        Assert.Equal(RecorderState.Saved, recorder.State);
    }

    [Fact]
    public async Task ResumeCleanupFailureKeepsReservationUntilBothSourcesCanBeStopped()
    {
        var reservation = new Reservation(); var starts = 0; var stops = 0;
        var recorder = new RecorderController(() => reservation,
            (_, _) => ++starts == 1 ? Task.CompletedTask
                : Task.FromException(new RecorderCleanupException("source still owned", new IOException())),
            () => Task.FromResult(new[] { (float)++stops }),
            audio => { Assert.Equal(new float[] { 1, 2 }, audio); return Task.FromResult("retained.wav"); });
        await recorder.StartAsync(true, true); await recorder.PauseAsync();
        await Assert.ThrowsAsync<RecorderCleanupException>(recorder.ResumeAsync);
        Assert.Equal(RecorderState.Recording, recorder.State); Assert.Equal(0, reservation.Released);
        await recorder.ShutdownAsync();
        Assert.Equal(2, stops); Assert.Equal(1, reservation.Released);
    }

    [Fact]
    public void TimelineFitRetainsLeadingSilenceAndRemovesDrainTailAtSharedBoundary()
    {
        var end = TimeSpan.FromSeconds(4.0 / RecorderSegments.SampleRate);
        Assert.Equal(new float[] { 0, 0, 0.7f, 0 }, RecorderSegments.FitTimeline([0, 0, 0.7f], end));
        Assert.Equal(new float[] { 0, 0.4f, 0, 0 }, RecorderSegments.FitTimeline([0, 0.4f, 0, 0, 0.9f], end));
    }

    [Fact]
    public void ActiveLimitCountsSamplesAcrossSegmentsAndNeverPauseTime()
    {
        var segments = new RecorderSegments();
        var second = new float[RecorderSegments.SampleRate];
        for (var i = 0; i < 3601; i++) segments.Add(second);
        Assert.Equal(RecorderSegments.MaximumSamples, segments.SampleCount);
        Assert.Equal(TimeSpan.FromHours(1), segments.Duration);
        segments.Clear(); Assert.Equal(TimeSpan.Zero, segments.Duration);
    }
}
