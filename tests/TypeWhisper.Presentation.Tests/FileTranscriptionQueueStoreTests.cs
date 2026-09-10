using System.Text.Json.Nodes;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class FileTranscriptionQueueStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "file-queue-store-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_root, "recovery.json");
    public FileTranscriptionQueueStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private FileTranscriptionRecoveryEntry Entry(FileTranscriptionRecoveryStatus status = FileTranscriptionRecoveryStatus.Queued,
        FileTranscriptionAcceptanceReceipt receipt = FileTranscriptionAcceptanceReceipt.None)
    {
        var path = Path.Combine(_root, Guid.NewGuid() + ".wav");
        File.WriteAllBytes(path, [1, 2, 3]);
        return new(Guid.NewGuid(), path, FileTranscriptionSourceFingerprint.Capture(path), status,
            status == FileTranscriptionRecoveryStatus.Ready
                ? new("Formatted text", "provider", "model", 2, [new("Original provider segment", 0.25, 1.75)]) : null, receipt);
    }
    private FileTranscriptionQueueStore Save(params FileTranscriptionRecoveryEntry[] entries)
    {
        var store = new FileTranscriptionQueueStore(StorePath);
        Assert.True(store.TrySetEnabled(true));
        Assert.True(store.TrySave(entries), store.Error);
        return store;
    }

    [Fact]
    public void MissingStoreIsOffAndDoesNotWriteUntilExplicitEnable()
    {
        var store = new FileTranscriptionQueueStore(StorePath);
        Assert.False(store.Enabled);
        Assert.Empty(store.Entries);
        Assert.False(store.TrySave([Entry()]));
        Assert.False(File.Exists(StorePath));
        Assert.True(store.TrySetEnabled(true));
        Assert.True(new FileTranscriptionQueueStore(StorePath).Enabled);
    }

    [Fact]
    public void DisableClearsPersistedResultsWithoutDeletingSources()
    {
        var entry = Entry(FileTranscriptionRecoveryStatus.Ready, FileTranscriptionAcceptanceReceipt.Completed);
        var store = Save(entry);
        Assert.True(store.TrySetEnabled(false));
        var restored = new FileTranscriptionQueueStore(StorePath);
        Assert.False(restored.Enabled);
        Assert.Empty(restored.Entries);
        Assert.DoesNotContain("Formatted text", File.ReadAllText(StorePath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(entry.SourcePath));
    }

    [Fact]
    public void ReadyResultRestoresOfflineWithStableIdentityAndOriginalSegments()
    {
        var entry = Entry(FileTranscriptionRecoveryStatus.Ready, FileTranscriptionAcceptanceReceipt.Completed);
        Save(entry);
        File.Delete(entry.SourcePath);
        var restored = Assert.Single(new FileTranscriptionQueueStore(StorePath).Entries);
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(entry.Source, restored.Source);
        Assert.Equal(FileTranscriptionRecoveryStatus.Ready, restored.Status);
        var output = restored.Result!.ToOutput();
        Assert.Equal("Formatted text", output.Text);
        Assert.Equal(new TranscriptionSegment("Original provider segment", 0.25, 1.75), Assert.Single(output.Segments));
        Assert.True(FileTranscriptionQueue.HasSubtitles(output));
        Assert.Null(output.PendingHistory);
        Assert.Empty(output.AppliedSnippetIds);
    }

    [Fact]
    public void SideEffectPayloadIsNeverPartOfRecoveryResult()
    {
        var output = new FileTranscriptionOutput("Accepted", "provider", "model", 1, [])
        {
            AppliedSnippetIds = ["secret-snippet-id"],
            PendingHistory = new() { Id = "history-id", Timestamp = DateTime.UtcNow, RawText = "private raw text", FinalText = "Accepted" }
        };
        var entry = Entry(FileTranscriptionRecoveryStatus.Ready, FileTranscriptionAcceptanceReceipt.Pending)
            with { Result = FileTranscriptionRecoveryResult.FromOutput(output) };
        Save(entry);
        var json = File.ReadAllText(StorePath);
        Assert.DoesNotContain("secret-snippet-id", json);
        Assert.DoesNotContain("private raw text", json);
        Assert.DoesNotContain("PendingHistory", json);
        Assert.DoesNotContain("AppliedSnippetIds", json);
    }

    [Fact]
    public void ProcessingRestoresInterruptedAndPendingReceiptWarnsWithoutReplaying()
    {
        var processing = Entry(FileTranscriptionRecoveryStatus.Processing);
        var pending = Entry(FileTranscriptionRecoveryStatus.Ready, FileTranscriptionAcceptanceReceipt.Pending);
        Save(processing, pending);
        var jobs = new FileTranscriptionQueueStore(StorePath).Entries;
        Assert.Equal(FileTranscriptionRecoveryStatus.Interrupted, jobs[0].Status);
        Assert.Contains("Retry", jobs[0].RecoveryNotice!);
        Assert.Equal(FileTranscriptionAcceptanceReceipt.Pending, jobs[1].Receipt);
        Assert.Contains("not repeated", jobs[1].RecoveryNotice!);
        Assert.Null(jobs[1].Result!.ToOutput().PendingHistory);
    }

    [Fact]
    public void FingerprintDetectsSizeOrTimestampChangesWithoutReadingAudio()
    {
        var entry = Entry();
        Assert.True(entry.Source!.Matches(entry.SourcePath));
        File.SetLastWriteTimeUtc(entry.SourcePath, entry.Source.LastWriteTimeUtc.AddSeconds(2));
        Assert.False(entry.Source.Matches(entry.SourcePath));
        File.WriteAllBytes(entry.SourcePath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(entry.SourcePath, entry.Source.LastWriteTimeUtc);
        Assert.False(entry.Source.Matches(entry.SourcePath));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing-field")]
    [InlineData("fingerprint-field")]
    [InlineData("invalid-receipt")]
    [InlineData("malformed")]
    public void InvalidSchemaPreservesBytesAndBlocksAllWrites(string fault)
    {
        Save(Entry());
        var json = JsonNode.Parse(File.ReadAllText(StorePath))!.AsObject();
        switch (fault)
        {
            case "version": json["Version"] = 99; break;
            case "unknown": json["FutureMetadata"] = "keep"; break;
            case "missing-field": json.Remove("Enabled"); break;
            case "fingerprint-field": json["Jobs"]![0]!["Source"]!.AsObject().Remove("SizeBytes"); break;
            case "invalid-receipt": json["Jobs"]![0]!["Receipt"] = "Completed"; break;
        }
        var text = fault == "malformed" ? "broken" : json.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"Version\":1", "\"Version\":1,\"version\":1", StringComparison.Ordinal);
        File.WriteAllText(StorePath, text);
        var store = new FileTranscriptionQueueStore(StorePath);
        Assert.False(store.Enabled);
        Assert.NotNull(store.Error);
        Assert.False(store.TrySetEnabled(false));
        Assert.False(store.TrySetEnabled(true));
        Assert.False(store.TrySave([]));
        Assert.Equal(text, File.ReadAllText(StorePath));
    }

    [Fact]
    public void BoundsAndDuplicateJobsDoNotReplacePreviousCheckpoint()
    {
        var entry = Entry();
        var store = Save(entry);
        var before = File.ReadAllBytes(StorePath);
        Assert.False(store.TrySave(Enumerable.Range(0, 21).Select(_ => Entry()).ToArray()));
        Assert.False(store.TrySave([entry, entry]));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Equal(entry.Id, Assert.Single(store.Entries).Id);
    }

    [Fact]
    public void InvalidSubtitleTimingKeepsTextAndDropsOnlyUnusableSegments()
    {
        var invalid = Entry(FileTranscriptionRecoveryStatus.Ready, FileTranscriptionAcceptanceReceipt.Completed);
        var store = Save(invalid with { Result = invalid.Result! with { Segments = [new("bad", double.NaN, 1)] } });
        var restored = Assert.Single(new FileTranscriptionQueueStore(StorePath).Entries).Result!;
        Assert.Equal("Formatted text", restored.Text);
        Assert.Empty(restored.Segments);
        Assert.Contains("Invalid subtitle timing", restored.Warning!);
        Assert.True(store.Enabled);
    }

    [Fact]
    public void ExplicitDiscardResetsInvalidCheckpointWithoutDeletingAnySource()
    {
        var entry = Entry();
        File.WriteAllText(StorePath, "invalid snapshot");
        var store = new FileTranscriptionQueueStore(StorePath);
        Assert.False(store.TrySetEnabled(false));
        Assert.Equal("invalid snapshot", File.ReadAllText(StorePath));
        Assert.True(store.TryDiscardSavedData());
        var restored = new FileTranscriptionQueueStore(StorePath);
        Assert.False(restored.Enabled);
        Assert.Null(restored.Error);
        Assert.True(File.Exists(entry.SourcePath));
        Assert.True(store.TrySetEnabled(true));
    }

    [Fact]
    public void FilesystemFailureKeepsLastSuccessfulOptionAndMemoryAndCleansTemporaryFile()
    {
        var entry = Entry();
        var store = Save(entry);
        var backup = StorePath + ".backup";
        File.Move(StorePath, backup);
        var before = File.ReadAllBytes(backup);
        Directory.CreateDirectory(StorePath);
        Assert.False(store.TrySetEnabled(false));
        Assert.True(store.Enabled);
        Assert.Equal(entry.Id, Assert.Single(store.Entries).Id);
        Assert.NotNull(store.Error);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Equal(before, File.ReadAllBytes(backup));
    }

    [Fact]
    public void EnablingWithInvalidCheckpointDoesNotPersistPartialConsent()
    {
        var store = new FileTranscriptionQueueStore(StorePath);
        Assert.True(store.TrySetEnabled(false));
        var before = File.ReadAllBytes(StorePath);
        var entry = Entry();
        Assert.False(store.TrySetEnabled(true, [entry, entry]));
        Assert.False(store.Enabled);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.False(new FileTranscriptionQueueStore(StorePath).Enabled);
    }
}
