using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class FileTranscriptionAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "file-acceptance-" + Guid.NewGuid());
    private string HistoryPath => Path.Combine(_root, "history.json");
    private string SnippetPath => Path.Combine(_root, "snippets.json");
    public FileTranscriptionAcceptanceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SnippetPath, JsonSerializer.Serialize(new[]
        { new Snippet { Id = "sig", Trigger = "sig", Replacement = "Expanded" } }));
    }
    public void Dispose() => Directory.Delete(_root, true);
    private static FileTranscriptionOutput Output(bool allowHistory = true) => new("Expanded", "provider", "model", 2, [])
    {
        AppliedSnippetIds = ["sig"],
        PendingHistory = allowHistory ? new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow,
            RawText = "sig", FinalText = "Expanded", DurationSeconds = 2,
            SourceKind = "file", EngineUsed = "provider", ModelUsed = "model"
        } : null
    };
    private FileTranscriptionQueue Queue()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Path.Combine(_root, "source.wav")));
        return queue;
    }
    private string? Commit(FileTranscriptionOutput result, HistoryService history, bool save = true) =>
        FileTranscriptionAcceptance.Commit(result, history, new() { SaveToHistory = save },
            ids => DictationLexiconSnapshot.RecordUsage(SnippetPath, ids));

    [Fact]
    public async Task CancellationBeforeAcceptanceWritesNothingAndRetryPersistsOnceAcrossRestart()
    {
        var history = new HistoryService(HistoryPath);
        await history.EnsureLoadedAsync();
        var queue = Queue();
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = Output();
        var run = queue.RunAsync((_, _, _) => release.Task, result => Commit(result, history));
        queue.Cancel();
        release.SetResult(prepared);
        await run;
        Assert.Empty(history.Records);
        Assert.False(File.Exists(HistoryPath));
        Assert.Equal(0, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
        var job = Assert.Single(queue.Jobs);
        Assert.True(queue.Retry(job));
        await queue.RunAsync((_, _, _) => Task.FromResult(prepared), result => Commit(result, history));
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal(prepared.PendingHistory, Assert.Single(new HistoryService(HistoryPath).Records));
        Assert.Equal(1, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
        await queue.RunAsync((_, _, _) => throw new InvalidOperationException("Ready output must not repeat."), result => Commit(result, history));
        Assert.Single(history.Records);
        Assert.Equal(1, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HistoryRequiresStartPermissionAndCurrentPermission(bool allowedAtStart, bool allowedNow)
    {
        var history = new HistoryService(HistoryPath);
        await history.EnsureLoadedAsync();
        var queue = Queue();
        await queue.RunAsync((_, _, _) => Task.FromResult(Output(allowedAtStart)), result => Commit(result, history, allowedNow));
        Assert.Equal(FileTranscriptionStatus.Ready, Assert.Single(queue.Jobs).Status);
        Assert.Empty(history.Records);
        Assert.False(File.Exists(HistoryPath));
        Assert.Equal(1, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Fact]
    public async Task HistoryFailureStillCountsAcceptedExpansionAndKeepsResultWithWarning()
    {
        var history = new HistoryService(HistoryPath);
        await history.EnsureLoadedAsync();
        Directory.CreateDirectory(HistoryPath);
        var queue = Queue();
        await queue.RunAsync((_, _, _) => Task.FromResult(Output()), result => Commit(result, history));
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal("Expanded", job.Result!.Text);
        Assert.Contains("History could not be saved", job.Result.Warning!);
        Assert.Empty(history.Records);
        Assert.Equal(1, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Fact]
    public async Task CancellationFromHistoryNotificationCannotUndoAlreadyAcceptedResult()
    {
        var history = new HistoryService(HistoryPath);
        await history.EnsureLoadedAsync();
        var queue = Queue();
        history.RecordsChanged += queue.Cancel;
        await queue.RunAsync((_, _, _) => Task.FromResult(Output()), result => Commit(result, history));
        Assert.Equal(FileTranscriptionStatus.Ready, Assert.Single(queue.Jobs).Status);
        Assert.Single(history.Records);
        Assert.Equal(1, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }
}
