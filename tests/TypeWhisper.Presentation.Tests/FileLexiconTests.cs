using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class FileLexiconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "file-lexicon-" + Guid.NewGuid());
    private string DictionaryPath => Path.Combine(_root, "dictionary.json");
    private string SnippetPath => Path.Combine(_root, "snippets.json");
    public FileLexiconTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private void WriteSnippets(params Snippet[] entries) => File.WriteAllText(SnippetPath, JsonSerializer.Serialize(entries));
    private DictationLexiconSnapshot Snapshot() => DictationLexiconSnapshot.Load(DictionaryPath, SnippetPath);
    private static Task<DictationLexiconSnapshot.Result> Process(DictationLexiconSnapshot snapshot, string text,
        Func<CancellationToken, Task<string>>? clipboard = null, CancellationToken ct = default,
        TranscriptionTask task = TranscriptionTask.Transcribe) => snapshot.ProcessAsync(text,
            new() { TranscriptionNumberNormalizationEnabled = false }, "en", "en", false,
            clipboard ?? (_ => throw new InvalidOperationException("Clipboard must not be read.")),
            ct, task, null, "test-provider", "test-model");
    private static FileTranscriptionOutput Output(DictationLexiconSnapshot.Result result) =>
        new(result.Text, "test-provider", "test-model", 2, [new("Original segment", 0, 2)],
            result.Warnings.Count == 0 ? null : string.Join(" · ", result.Warnings))
        { AppliedSnippetIds = result.AppliedSnippetIds };
    private FileTranscriptionQueue Queue()
    {
        var queue = new FileTranscriptionQueue();
        Assert.Null(queue.Add(Path.Combine(_root, "source.wav")));
        return queue;
    }

    [Fact]
    public void DeleteCorrectionGroupPersistsAllVariantsAndPreservesOtherEntries()
    {
        var store = new Lexicon(DictionaryPath, SnippetPath);
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "wrong one", "Correct")));
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "wrong two", "Correct") { Enabled = false }));
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "other", "Different")));
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Word, "Correct")));
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Snippet, "sig", "Correct")));
        Assert.True(store.RemoveCorrectionGroup("Correct"));
        var reloaded = new Lexicon(DictionaryPath, SnippetPath);
        Assert.DoesNotContain(reloaded.Entries, entry => entry.Kind == LexiconKind.Correction && entry.Value == "Correct");
        Assert.Equal(3, reloaded.Entries.Count);
        Assert.Contains(reloaded.Entries, entry => entry.Kind == LexiconKind.Word && entry.Key == "Correct");
        Assert.Contains(reloaded.Entries, entry => entry.Kind == LexiconKind.Snippet && entry.Key == "sig");
        Assert.False(reloaded.RemoveCorrectionGroup("Correct"));
    }

    [Fact]
    public async Task SnapshotUsesSnippetThenCorrectionsWithoutWritingAndMergesUsageIntoCurrentCatalog()
    {
        WriteSnippets(new Snippet() { Id = "signature", Trigger = "sig", Replacement = "mistake", UsageCount = 4 });
        File.WriteAllText(DictionaryPath, JsonSerializer.Serialize(new[]
        { new DictionaryEntry { Id = "fix", EntryType = DictionaryEntryType.Correction, Original = "mistake", Replacement = "correct" } }));
        var snapshot = Snapshot();
        // An edit made while decoding must survive the older request's usage commit.
        WriteSnippets(new Snippet() { Id = "signature", Trigger = "sig", Replacement = "New content", UsageCount = 9 });
        var before = File.ReadAllBytes(SnippetPath);
        var result = await Process(snapshot, "sig sig");
        Assert.Equal("correct correct", result.Text);
        Assert.Equal("signature", Assert.Single(result.AppliedSnippetIds));
        Assert.Equal(before, File.ReadAllBytes(SnippetPath));
        var queue = Queue();
        await queue.RunAsync((_, _, _) => Task.FromResult(Output(result)),
            accepted => DictationLexiconSnapshot.RecordUsage(SnippetPath, accepted.AppliedSnippetIds));
        var current = Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath));
        Assert.Equal(10, current.UsageCount);
        Assert.Equal("New content", current.Replacement);
        await queue.RunAsync((_, _, _) => throw new InvalidOperationException("Ready jobs must not rerun."));
        var job = Assert.Single(queue.Jobs);
        Assert.False(queue.Retry(job));
        Assert.Equal("correct correct", FileTranscriptionQueue.Export(job, "txt"));
        Assert.Contains("Original segment", FileTranscriptionQueue.Export(job, "srt"));
        Assert.DoesNotContain("correct correct", FileTranscriptionQueue.Export(job, "vtt"));
        Assert.Equal(10, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Fact]
    public async Task LateCanceledResultDoesNotCountAndRetryCountsOnce()
    {
        WriteSnippets(new Snippet() { Id = "signature", Trigger = "sig", Replacement = "Expanded" });
        var output = Output(await Process(Snapshot(), "sig"));
        var release = new TaskCompletionSource<FileTranscriptionOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = Queue();
        var commits = 0;
        string? Commit(FileTranscriptionOutput accepted)
        { commits++; return DictationLexiconSnapshot.RecordUsage(SnippetPath, accepted.AppliedSnippetIds); }
        var run = queue.RunAsync((_, _, _) => release.Task, Commit);
        queue.Cancel();
        release.SetResult(output);
        await run;
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(FileTranscriptionStatus.Canceled, job.Status);
        Assert.Equal(0, commits);
        Assert.Equal(0, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
        Assert.True(queue.Retry(job));
        await queue.RunAsync((_, _, _) => Task.FromResult(output), Commit);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal(1, commits);
        Assert.Equal(1, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Fact]
    public async Task ClipboardProbeReadsOnlyMatchingExpansionAndNeverCounts()
    {
        WriteSnippets(new Snippet() { Id = "link", Trigger = "link", Replacement = "See {clipboard}" });
        var snapshot = Snapshot();
        var reads = 0;
        Task<string> Clipboard(CancellationToken _) { reads++; return Task.FromResult("clipboard text"); }
        Assert.Equal("ordinary text", (await Process(snapshot, "ordinary text", Clipboard)).Text);
        Assert.Equal(0, reads);
        var result = await Process(snapshot, "link", Clipboard);
        Assert.Equal("See clipboard text", result.Text);
        Assert.Equal(1, reads);
        Assert.Equal("link", Assert.Single(result.AppliedSnippetIds));
        Assert.Equal(0, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Fact]
    public async Task FailedClipboardRetainsTextWithoutUsageAndCancellationPropagates()
    {
        WriteSnippets(new Snippet() { Id = "link", Trigger = "link", Replacement = "{clipboard}" });
        var snapshot = Snapshot();
        var result = await Process(snapshot, "link", _ => throw new IOException("Clipboard unavailable"));
        Assert.Equal("link", result.Text);
        Assert.Empty(result.AppliedSnippetIds);
        Assert.NotEmpty(result.Warnings);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Process(snapshot, "link", async ct =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "late"; }, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, Assert.Single(DictationSnippetSnapshot.ReadEntries(SnippetPath)).UsageCount);
    }

    [Fact]
    public async Task CorruptSnapshotsWarnAndKeepDecodedText()
    {
        File.WriteAllText(DictionaryPath, "broken");
        File.WriteAllText(SnippetPath, "broken");
        var result = await Process(Snapshot(), "decoded text");
        Assert.Equal("decoded text", result.Text);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Empty(result.AppliedSnippetIds);
    }

    [Fact]
    public async Task DeletedSnippetIsNotRestoredAndUsageFailureKeepsAcceptedResult()
    {
        WriteSnippets(new Snippet() { Id = "sig", Trigger = "sig", Replacement = "Expanded" });
        var output = Output(await Process(Snapshot(), "sig"));
        WriteSnippets();
        Assert.Null(DictationLexiconSnapshot.RecordUsage(SnippetPath, output.AppliedSnippetIds));
        Assert.Empty(DictationSnippetSnapshot.ReadEntries(SnippetPath));
        // A directory target fails on Windows and Unix without relying on FileShare locks.
        var blocked = Path.Combine(_root, "blocked.json");
        Directory.CreateDirectory(blocked);
        var queue = Queue();
        await queue.RunAsync((_, _, _) => Task.FromResult(output),
            accepted => DictationLexiconSnapshot.RecordUsage(blocked, accepted.AppliedSnippetIds));
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal("Expanded", job.Result!.Text);
        Assert.Contains("could not be saved", job.Result.Warning!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedEmptyOrFailedDecodeNeverCallsAcceptance(bool failDecode)
    {
        var queue = Queue();
        var commits = 0;
        await queue.RunAsync((_, _, _) => failDecode
            ? Task.FromException<FileTranscriptionOutput>(new IOException("Decode failed"))
            : Task.FromResult(new FileTranscriptionOutput(" ", "test", "model", 1, [])),
            _ => { commits++; return null; });
        Assert.Equal(FileTranscriptionStatus.Failed, Assert.Single(queue.Jobs).Status);
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task AcceptanceFailurePreservesReadyResultAndEarlierWarnings()
    {
        var queue = Queue();
        await queue.RunAsync((_, _, _) => Task.FromResult(new FileTranscriptionOutput(
            "Accepted text", "test", "model", 1, [], "Earlier warning")),
            _ => throw new IOException("Write failed"));
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(FileTranscriptionStatus.Ready, job.Status);
        Assert.Equal("Accepted text", job.Result!.Text);
        Assert.StartsWith("Earlier warning · ", job.Result.Warning!);
        Assert.Contains("could not be saved", job.Result.Warning!);
    }

    [Theory]
    [InlineData(TranscriptionTask.Transcribe, false, "parakeet-tdt-0.6b", true, 1, true)]
    [InlineData(TranscriptionTask.Translate, false, "parakeet-tdt-0.6b", true, 1, false)]
    [InlineData(TranscriptionTask.Transcribe, true, "parakeet-tdt-0.6b", true, 1, false)]
    [InlineData(TranscriptionTask.Transcribe, false, "canary-180m", true, 1, false)]
    [InlineData(TranscriptionTask.Transcribe, false, "parakeet-tdt-0.6b", false, 1, false)]
    [InlineData(TranscriptionTask.Transcribe, false, "parakeet-tdt-0.6b", true, 0, false)]
    public void CtcRequiresLocalParakeetTranscriptionAndActualTiming(TranscriptionTask task, bool registry,
        string model, bool ready, int timings, bool expected) =>
        Assert.Equal(expected, DictationLexiconSnapshot.CanRefineWithCtc(task, registry, model, ready, timings));

    [Fact]
    public async Task TranslatedOutputStillUsesExplicitTextRules()
    {
        WriteSnippets(new Snippet() { Id = "sig", Trigger = "sig", Replacement = "English signature" });
        var result = await Process(Snapshot(), "sig", task: TranscriptionTask.Translate);
        Assert.Equal("English signature", result.Text);
        Assert.Equal("sig", Assert.Single(result.AppliedSnippetIds));
    }
}
