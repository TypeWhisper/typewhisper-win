using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class TranscriptFileExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-export-" + Guid.NewGuid());
    public TranscriptFileExportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BatchPreservesExistingFilesAndHandlesSameStemAndUnicode()
    {
        File.WriteAllText(Path.Combine(_root, "meeting.txt"), "existing document");
        var queue = new FileTranscriptionQueue();
        queue.Add(Path.Combine(_root, "meeting.wav")); queue.Add(Path.Combine(_root, "meeting.mp3"));
        queue.Add(Path.Combine(_root, "Grüße.wav"));
        await queue.RunAsync((_,_,_) => Task.FromResult(new FileTranscriptionOutput("Grüße, 世界!", "p", "m", 1, [])));
        Assert.Empty(await TranscriptFileExport.ExportBatchAsync(queue.Jobs, _root, "txt"));
        Assert.Equal("existing document", File.ReadAllText(Path.Combine(_root, "meeting.txt")));
        Assert.Equal(4, Directory.GetFiles(_root).Length);
        Assert.Equal("Grüße, 世界!", File.ReadAllText(Path.Combine(_root, "Grüße.txt")));
    }

    [Fact]
    public async Task MissingTimingDoesNotInventSubtitlesAndOtherResultsContinue()
    {
        var queue = new FileTranscriptionQueue(); queue.Add(Path.Combine(_root, "a.wav")); queue.Add(Path.Combine(_root, "b.wav"));
        await queue.RunAsync((path,_,_) => Task.FromResult(new FileTranscriptionOutput("Actual text", "p", "m", 2,
            path.EndsWith("a.wav") ? [] : [new("Timed text", 0, 1)])));
        var failures = await TranscriptFileExport.ExportBatchAsync(queue.Jobs, _root, "srt");
        Assert.Single(failures); Assert.False(File.Exists(Path.Combine(_root, "a.srt")));
        Assert.Contains("Timed text", File.ReadAllText(Path.Combine(_root, "b.srt")));
    }

    [Fact]
    public async Task InterruptedPublicationOnlyAcceptsAnIdenticalCheckpointedFile()
    {
        var path = Path.Combine(_root, "result.txt"); File.WriteAllText(path, "original");
        await TranscriptFileExport.PublishAsync(path, "original", default, verifyExisting: true);
        await Assert.ThrowsAsync<IOException>(() => TranscriptFileExport.PublishAsync(path, "replacement", default, verifyExisting: true));
        await Assert.ThrowsAsync<IOException>(() => TranscriptFileExport.PublishAsync(path, "original", default));
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public async Task CanceledWritePublishesNothingAndCleansTemporaryFiles()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranscriptFileExport.PublishAsync(Path.Combine(_root, "result.txt"), "text", canceled.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
