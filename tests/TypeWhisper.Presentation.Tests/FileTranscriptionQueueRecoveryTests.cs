using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class FileTranscriptionQueueRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queue-recovery-integration-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_root, "queue.json");
    private string SourcePath => Path.Combine(_root, "source.wav");
    public FileTranscriptionQueueRecoveryTests()
    { Directory.CreateDirectory(_root); File.WriteAllBytes(SourcePath, [1, 2, 3]); }
    public void Dispose() => Directory.Delete(_root, true);
    private FileTranscriptionQueue Create()
    {
        var queue = new FileTranscriptionQueue(new FileTranscriptionQueueStore(StorePath));
        Assert.True(queue.SetRecoveryEnabled(true));
        Assert.Null(queue.Add(SourcePath));
        return queue;
    }
    private static FileTranscriptionOutput Output() => new("Formatted", "provider", "model", 2,
        [new("Provider segment", 0, 2)]) { AppliedSnippetIds = ["sig"] };

    [Fact]
    public async Task ReadyIsDurableBeforeAcceptanceAndRestartDoesNotReplay()
    {
        var queue = Create();
        var commits = 0;
        await queue.RunAsync((_, _, _) => Task.FromResult(Output()), _ =>
        {
            commits++;
            var durable = Assert.Single(new FileTranscriptionQueueStore(StorePath).Entries);
            Assert.Equal(FileTranscriptionRecoveryStatus.Ready, durable.Status);
            Assert.Equal(FileTranscriptionAcceptanceReceipt.Pending, durable.Receipt);
            Assert.Empty(durable.Result!.ToOutput().AppliedSnippetIds);
            return null;
        });
        var saved = Assert.Single(new FileTranscriptionQueueStore(StorePath).Entries);
        Assert.Equal(FileTranscriptionAcceptanceReceipt.Completed, saved.Receipt);
        File.Delete(SourcePath);
        var restored = new FileTranscriptionQueue(new FileTranscriptionQueueStore(StorePath));
        Assert.False(restored.Running);
        Assert.Equal(queue.Jobs[0].Id, restored.Jobs[0].Id);
        await restored.RunAsync((_, _, _) => throw new InvalidOperationException("Must not decode recovered Ready jobs."), _ => { commits++; return null; });
        Assert.Equal(1, commits);
        Assert.Equal("Formatted", FileTranscriptionQueue.Export(restored.Jobs[0], "txt"));
        Assert.Contains("Provider segment", FileTranscriptionQueue.Export(restored.Jobs[0], "srt"));
    }

    [Fact]
    public async Task InProgressCheckpointRestoresInterruptedUntilExplicitRetry()
    {
        var queue = Create();
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = queue.RunAsync((_, _, _) => release.Task);
        var restored = new FileTranscriptionQueue(new FileTranscriptionQueueStore(StorePath));
        var job = Assert.Single(restored.Jobs);
        Assert.Equal(FileTranscriptionStatus.Canceled, job.Status);
        Assert.Contains("interrupted", job.Stage);
        var calls = 0;
        await restored.RunAsync((_, _, _) => { calls++; return Task.FromResult(Output()); });
        Assert.Equal(0, calls);
        // Finish the original owner before allowing the restored instance to write.
        queue.Cancel(); release.SetResult(Output()); await run;
        Assert.True(restored.Retry(job));
        await restored.RunAsync((_, _, _) => { calls++; return Task.FromResult(Output()); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedReadyCheckpointPreservesTextButSkipsAcceptanceEffects()
    {
        var queue = Create();
        var commits = 0;
        await queue.RunAsync((_, _, _) =>
        {
            File.Move(StorePath, StorePath + ".before-result");
            Directory.CreateDirectory(StorePath);
            return Task.FromResult(Output());
        }, _ => { commits++; return null; });
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal("Formatted", FileTranscriptionQueue.Export(job, "txt"));
        Assert.Equal(0, commits);
        Assert.Contains("were not recorded", job.Result!.Warning!);
        Assert.NotNull(queue.RecoveryError);
    }

    [Fact]
    public async Task FailedCompletionCheckpointRestoresPendingWithoutRepeatingEffects()
    {
        var queue = Create();
        var commits = 0;
        var pendingBackup = StorePath + ".pending";
        await queue.RunAsync((_, _, _) => Task.FromResult(Output()), _ =>
        {
            commits++;
            File.Move(StorePath, pendingBackup);
            Directory.CreateDirectory(StorePath);
            return null;
        });
        Assert.NotNull(queue.RecoveryError);
        Directory.Delete(StorePath);
        File.Move(pendingBackup, StorePath);
        var restored = new FileTranscriptionQueue(new FileTranscriptionQueueStore(StorePath));
        var job = Assert.Single(restored.Jobs);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Contains("not repeated", job.Result!.Warning!);
        await restored.RunAsync((_, _, _) => throw new InvalidOperationException("No automatic rerun."), _ => { commits++; return null; });
        Assert.Equal(1, commits);
    }

    [Fact]
    public async Task ChangedSourceRequiresExplicitRetryAndDiscardKeepsMemoryAndMedia()
    {
        var queue = Create();
        File.WriteAllBytes(SourcePath, [9, 8, 7, 6]);
        var restored = new FileTranscriptionQueue(new FileTranscriptionQueueStore(StorePath));
        var calls = 0;
        await restored.RunAsync((_, _, _) => { calls++; return Task.FromResult(Output()); });
        var job = Assert.Single(restored.Jobs);
        Assert.Equal(FileTranscriptionStatus.Failed, job.Status);
        Assert.Contains("changed", job.Stage);
        Assert.Equal(0, calls);
        Assert.True(restored.Retry(job));
        await restored.RunAsync((_, _, _) => { calls++; return Task.FromResult(Output()); });
        Assert.Equal(1, calls);
        Assert.True(restored.DiscardRecoveryData());
        Assert.Equal("Formatted", job.Result!.Text);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(SourcePath));
        Assert.Empty(new FileTranscriptionQueueStore(StorePath).Entries);
        Assert.False(restored.RecoveryEnabled);
    }

    [Fact]
    public async Task InvalidSubtitlesDoNotBlockAcceptanceOrTextRecovery()
    {
        var queue = Create();
        var commits = 0;
        await queue.RunAsync((_, _, _) => Task.FromResult(Output() with { Segments = [new("bad", double.NaN, 1)] }),
            _ => { commits++; return null; });
        Assert.Equal(1, commits);
        var restored = new FileTranscriptionQueue(new FileTranscriptionQueueStore(StorePath));
        var job = Assert.Single(restored.Jobs);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal("Formatted", FileTranscriptionQueue.Export(job, "txt"));
        Assert.Empty(job.Result!.Segments);
        Assert.Contains("subtitle timing", job.Result.Warning!);
    }
}
