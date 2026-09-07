using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class FileTranscriptionQueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-file-queue-" + Guid.NewGuid());
    private static FileTranscriptionOutput Output(string text = "Actual decoded text") => new(text, "real-provider", "actual-model", 3,
        [new("First provider segment", 0.125, 1.25), new("Second provider segment", 1.5, 2.875)]);

    private string Source(string name)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [0, 17, 254, 42]);
        return path;
    }

    [Fact]
    public async Task RunsSeriallyAndIgnoresConcurrentRunAndMutations()
    {
        var queue = new FileTranscriptionQueue();
        var first = Source("first.wav");
        var second = Source("second.mp3");
        Assert.Null(queue.Add(first));
        Assert.Null(queue.Add(second));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var run = queue.RunAsync((path, stage, token) =>
        {
            calls.Add(path);
            stage("Decoding actual audio");
            return calls.Count == 1 ? release.Task : Task.FromResult(Output("second result"));
        });
        Assert.True(queue.Running);
        Assert.Single(calls);
        Assert.Equal("Decoding actual audio", queue.Jobs[0].Stage);
        Assert.Equal(FileTranscriptionStatus.Queued, queue.Jobs[1].Status);
        Assert.NotNull(queue.Add(Source("third.wav")));
        Assert.False(queue.Remove(queue.Jobs[0]));
        Assert.False(queue.Retry(queue.Jobs[0]));
        await queue.RunAsync((_, _, _) => throw new InvalidOperationException("A second run must not execute."));
        release.SetResult(Output());
        await run;
        Assert.Equal(new[] { first, second }, calls);
        Assert.False(queue.Running);
        Assert.All(queue.Jobs, job => Assert.Equal(FileTranscriptionStatus.Ready, job.Status));
        Assert.Equal("second result", queue.Jobs[1].Result!.Text);
        Assert.False(queue.Retry(queue.Jobs[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDrainsAndRejectsLateResultOrError(bool failLate)
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("active.wav")));
        Assert.Null(queue.Add(Source("pending.wav")));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string>? report = null;
        CancellationToken receivedToken = default;
        var calls = 0;
        var run = queue.RunAsync((_, stage, token) =>
        {
            calls++;
            report = stage;
            receivedToken = token;
            return release.Task;
        });
        queue.Cancel();
        Assert.True(receivedToken.IsCancellationRequested);
        Assert.True(queue.Running);
        Assert.False(queue.Retry(queue.Jobs[0]));
        report!("late progress");
        Assert.NotEqual("late progress", queue.Jobs[0].Stage);
        if (failLate) release.SetException(new IOException("late provider failure"));
        else release.SetResult(Output("must be discarded"));
        await run;
        Assert.Equal(1, calls);
        Assert.False(queue.Running);
        Assert.All(queue.Jobs, job =>
        {
            Assert.Equal(FileTranscriptionStatus.Canceled, job.Status);
            Assert.Null(job.Result);
            Assert.Throws<InvalidOperationException>(() => FileTranscriptionQueue.Export(job, "txt"));
        });
        report("even later progress");
        Assert.Equal("Canceled", queue.Jobs[0].Stage);
    }

    [Fact]
    public async Task FailureDoesNotStopOtherFilesAndRetryUsesOnlyExplicitlyRequeuedJob()
    {
        var queue = new FileTranscriptionQueue();
        var failed = Source("failure.wav");
        Assert.Null(queue.Add(failed));
        Assert.Null(queue.Add(Source("success.wav")));
        Action<string>? oldProgress = null;
        await queue.RunAsync((path, progress, _) =>
        {
            if (path == failed) { oldProgress = progress; throw new IOException("injected decoder failure"); }
            return Task.FromResult(Output());
        });
        Assert.Equal(FileTranscriptionStatus.Failed, queue.Jobs[0].Status);
        Assert.Equal(FileTranscriptionStatus.Ready, queue.Jobs[1].Status);
        var accepted = queue.Jobs[1].Result;
        oldProgress!("obsolete progress");
        Assert.Equal("injected decoder failure", queue.Jobs[0].Stage);
        Assert.False(queue.Retry(new FileTranscriptionJob(failed)));
        Assert.True(queue.Retry(queue.Jobs[0]));
        var calls = 0;
        await queue.RunAsync((path, _, _) =>
        {
            calls++;
            Assert.Equal(failed, path);
            return Task.FromResult(Output("retried result"));
        });
        Assert.Equal(1, calls);
        Assert.Equal("retried result", queue.Jobs[0].Result!.Text);
        Assert.Same(accepted, queue.Jobs[1].Result);
    }

    [Fact]
    public async Task CancelKeepsAlreadyCompletedResultAndCanceledJobCanBeRetried()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("done.wav")));
        Assert.Null(queue.Add(Source("cancel.wav")));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var run = queue.RunAsync((_, _, _) => ++calls == 1 ? Task.FromResult(Output()) : release.Task);
        queue.Cancel();
        release.SetResult(Output("late"));
        await run;
        Assert.Equal(FileTranscriptionStatus.Ready, queue.Jobs[0].Status);
        Assert.Equal(Output().Text, FileTranscriptionQueue.Export(queue.Jobs[0], "txt"));
        Assert.True(queue.Retry(queue.Jobs[1]));
        await queue.RunAsync((_, _, _) => Task.FromResult(Output("retry")));
        Assert.Equal("retry", queue.Jobs[1].Result!.Text);
    }

    [Fact]
    public async Task ProgressFromFailedAttemptCannotOverwriteRetryStage()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("retry.wav")));
        Action<string>? previousProgress = null;
        await queue.RunAsync((_, stage, _) =>
        {
            previousProgress = stage;
            throw new IOException("first attempt failed");
        });
        var job = Assert.Single(queue.Jobs);
        Assert.True(queue.Retry(job));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = queue.RunAsync((_, stage, _) =>
        {
            stage("Current attempt");
            return release.Task;
        });
        previousProgress!("Stale first attempt");
        var observedStage = job.Stage;
        release.SetResult(Output());
        await retry;
        Assert.Equal("Current attempt", observedStage);
    }

    [Fact]
    public async Task ExportUsesProviderSegmentsAndNeverModifiesOrDeletesSource()
    {
        var queue = new FileTranscriptionQueue();
        var path = Source("original.wav");
        var before = File.ReadAllBytes(path);
        Assert.Null(queue.Add(path));
        await queue.RunAsync((_, _, _) => Task.FromResult(Output()));
        var job = Assert.Single(queue.Jobs);
        Assert.Equal("Actual decoded text", FileTranscriptionQueue.Export(job, "txt"));
        Assert.Equal("1\n00:00:00,125 --> 00:00:01,250\nFirst provider segment\n\n2\n00:00:01,500 --> 00:00:02,875\nSecond provider segment\n\n",
            FileTranscriptionQueue.Export(job, "srt").ReplaceLineEndings("\n"));
        Assert.Equal("WEBVTT\n\n00:00:00.125 --> 00:00:01.250\nFirst provider segment\n\n00:00:01.500 --> 00:00:02.875\nSecond provider segment\n\n",
            FileTranscriptionQueue.Export(job, "vtt").ReplaceLineEndings("\n"));
        Assert.Throws<ArgumentException>(() => FileTranscriptionQueue.Export(job, "unsupported"));
        Assert.True(queue.Remove(job));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task UntimedProviderTextRemainsExportableWithoutFabricatedSubtitles()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("untimed.wav")));
        await queue.RunAsync((_, _, _) => Task.FromResult(Output() with { Segments = [] }));
        var job = Assert.Single(queue.Jobs);
        Assert.False(FileTranscriptionQueue.HasSubtitles(job.Result!));
        Assert.Equal(Output().Text, FileTranscriptionQueue.Export(job, "txt"));
        Assert.Throws<InvalidOperationException>(() => FileTranscriptionQueue.Export(job, "srt"));
        Assert.Throws<InvalidOperationException>(() => FileTranscriptionQueue.Export(job, "vtt"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void SubtitleDurationMustBeFiniteAndPositive(double duration) =>
        Assert.False(FileTranscriptionQueue.HasSubtitles(Output() with { Duration = duration }));

    [Fact]
    public void SubtitleSegmentsMustContainRealOrderedUsableTiming()
    {
        TranscriptionSegment[][] invalid =
        [
            [new("", 0, 1)], [new("text", -1, 1)], [new("text", 1, 1)],
            [new("text", double.NaN, 1)], [new("text", 0, double.PositiveInfinity)],
            [new("text", 0, 5)], [new("late", 2, 3), new("early", 0, 1)]
        ];
        foreach (var segments in invalid)
            Assert.False(FileTranscriptionQueue.HasSubtitles(Output() with { Segments = segments }));
    }

    [Fact]
    public async Task EmptyDecodeFailsWithoutInventingAResult()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("silence.wav")));
        await queue.RunAsync((_, _, _) => Task.FromResult(Output(" \n ")));
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(FileTranscriptionStatus.Failed, job.Status);
        Assert.Null(job.Result);
        Assert.Throws<InvalidOperationException>(() => FileTranscriptionQueue.Export(job, "txt"));
    }

    [Fact]
    public void ValidatesExtensionDuplicatesAndCapacityWithoutOpeningMedia()
    {
        var queue = new FileTranscriptionQueue();
        var path = Path.Combine(_directory, "first.WAV");
        Assert.Null(queue.Add(path));
        Assert.NotNull(queue.Add(path));
        Assert.NotNull(queue.Add(Path.Combine(_directory, "text.txt")));
        for (var i = 1; i < 20; i++) Assert.Null(queue.Add(Path.Combine(_directory, $"{i}.mp4")));
        Assert.NotNull(queue.Add(Path.Combine(_directory, "overflow.wav")));
        Assert.Equal(20, queue.Jobs.Count);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndDrainWaitsForLateDecoderAndIsReusable(bool failLate)
    {
        var queue = new FileTranscriptionQueue();
        Assert.True(queue.RunCompletion.IsCompletedSuccessfully);
        Assert.Null(queue.Add(Source("drain.wav")));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = queue.RunAsync((_, _, _) => release.Task);
        var firstDrain = queue.CancelAndDrainAsync();
        var secondDrain = queue.CancelAndDrainAsync();
        Assert.Same(run, queue.RunCompletion);
        Assert.Same(run, firstDrain);
        Assert.Same(firstDrain, secondDrain);
        Assert.False(firstDrain.IsCompleted);
        Assert.True(queue.Running);
        if (failLate) release.SetException(new IOException("late decoder failure"));
        else release.SetResult(Output("late result"));
        await Task.WhenAll(run, firstDrain, secondDrain);
        Assert.False(queue.Running);
        Assert.False(queue.IsShutdown);
        Assert.Equal(FileTranscriptionStatus.Canceled, Assert.Single(queue.Jobs).Status);
        Assert.Null(queue.Jobs[0].Result);
        Assert.True(queue.Retry(queue.Jobs[0]));
        await queue.RunAsync((_, _, _) => Task.FromResult(Output("retry after navigation")));
        Assert.Equal("retry after navigation", queue.Jobs[0].Result!.Text);
    }

    [Fact]
    public async Task ShutdownBlocksNewWorkImmediatelyAndDrainsBeforeCompleting()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("shutdown.wav")));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        var run = queue.RunAsync((_, _, token) => { received = token; return release.Task; });
        var shutdown = queue.ShutdownAsync();
        Assert.True(queue.IsShutdown);
        Assert.True(received.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        Assert.Same(shutdown, queue.ShutdownAsync());
        Assert.NotNull(queue.Add(Source("rejected.wav")));
        await queue.RunAsync((_, _, _) => throw new InvalidOperationException("Shutdown must block new decoders."));
        release.SetException(new IOException("late decoder error"));
        await Task.WhenAll(run, shutdown);
        Assert.False(queue.Retry(queue.Jobs[0]));
        Assert.NotNull(queue.Add(Source("still-rejected.wav")));
        await queue.RunAsync((_, _, _) => throw new InvalidOperationException("Shutdown must remain permanent."));
        Assert.True(queue.RunCompletion.IsCompletedSuccessfully);
        Assert.Null(queue.Jobs[0].Result);
    }

    [Fact]
    public async Task IdleShutdownIsIdempotentAndNeverStartsQueuedMedia()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("never-started.wav")));
        await queue.ShutdownAsync();
        await queue.ShutdownAsync();
        await queue.CancelAndDrainAsync();
        await queue.RunAsync((_, _, _) => throw new InvalidOperationException("No decoder should start."));
        Assert.True(queue.IsShutdown);
        Assert.False(queue.Running);
        Assert.Null(queue.Jobs[0].Result);
    }

    [Fact]
    public async Task CompletionExistsBeforeReentrantShutdownNotification()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Source("reentrant.wav")));
        Task? shutdown = null;
        queue.Changed += () =>
        {
            if (queue.Jobs[0].Status == FileTranscriptionStatus.Processing)
            {
                Assert.False(queue.RunCompletion.IsCompleted);
                shutdown = queue.ShutdownAsync();
            }
        };
        var run = queue.RunAsync((_, _, _) => throw new InvalidOperationException("Canceled before decoder entry."));
        await run;
        Assert.Same(run, shutdown);
        Assert.Equal(FileTranscriptionStatus.Canceled, queue.Jobs[0].Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
