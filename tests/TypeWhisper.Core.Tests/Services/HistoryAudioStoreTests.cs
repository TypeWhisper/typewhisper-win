using System.Text.Json.Nodes;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;

namespace TypeWhisper.Core.Tests.Services;

public sealed class HistoryAudioStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "history-audio-test-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => Path.Combine(_root, "history.json");
    private string AudioPath => Path.Combine(_root, "audio");
    private static readonly float[] Samples = [0, 0.25f, -0.25f, 1, -1];
    public HistoryAudioStoreTests() => Directory.CreateDirectory(_root);
    private HistoryService Service() => new(HistoryPath, audioStore: new HistoryAudioStore(AudioPath));
    private static TranscriptionRecord Record(string id = "one") => new()
    {
        Id = id, RawText = "original", FinalText = "final", Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow, SourceKind = "dictation"
    };
    private static HistoryAudioSaveResult Add(HistoryService service, string id = "one") =>
        service.TryAddRecordWithAudio(Record(id), Samples, 16000, () => true);

    [Fact]
    public void ExplicitAudioPersistsExactUnpaddedPcmAndResolvesAfterRestart()
    {
        var service = Service();
        var result = Add(service);
        Assert.True(result.Saved);
        Assert.Null(result.Warning);
        var path = service.ResolveAudioPath(result.Record.AudioFileName);
        Assert.NotNull(path);
        Assert.Equal(WavEncoder.Encode(Samples), File.ReadAllBytes(path));
        var restarted = Service();
        Assert.Equal(result.Record.AudioFileName, Assert.Single(restarted.Records).AudioFileName);
        Assert.Equal(path, restarted.ResolveAudioPath(result.Record.AudioFileName));
    }

    [Fact]
    public void AudioOffCreatesNoAudioDirectoryAndDoesNotGrantLaterPermission()
    {
        var service = Service();
        var calls = 0;
        var result = service.TryAddRecordWithAudio(Record(), Samples, 16000, () => ++calls > 1);
        Assert.True(result.Saved);
        Assert.Null(result.Record.AudioFileName);
        Assert.False(Directory.Exists(AudioPath));
    }

    [Fact]
    public void AudioPermissionWithdrawnAfterPreparationKeepsOnlyText()
    {
        var calls = 0;
        var result = Service().TryAddRecordWithAudio(Record(), Samples, 16000, () =>
        {
            if (++calls == 1) return true;
            Assert.Single(Directory.GetFiles(AudioPath, "*.wav"));
            return false;
        });
        Assert.True(result.Saved);
        Assert.Null(result.Record.AudioFileName);
        Assert.Null(result.Warning);
        Assert.Empty(Directory.GetFiles(AudioPath, "*.wav"));
    }

    [Fact]
    public void HistoryPermissionWithdrawnAtCommitSuppressesTextAndPreparedAudio()
    {
        var historyChecks = 0;
        var result = Service().TryAddRecordWithAudio(Record(), Samples, 16000, () => true,
            maySaveHistory: () =>
            {
                if (++historyChecks == 1) return true;
                Assert.Single(Directory.GetFiles(AudioPath, "*.wav"));
                return false;
            });
        Assert.False(result.Saved);
        Assert.True(result.Suppressed);
        Assert.Null(result.Warning);
        Assert.Null(result.Record.AudioFileName);
        Assert.False(File.Exists(HistoryPath));
        Assert.Empty(Directory.GetFiles(AudioPath, "*.wav"));
    }

    [Fact]
    public void CancellationAtFinalPermissionCheckCommitsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => Service().TryAddRecordWithAudio(Record(), Samples, 16000,
            () => { if (++calls == 2) cancellation.Cancel(); return true; }, cancellation.Token));
        Assert.False(File.Exists(HistoryPath));
        Assert.Empty(Directory.GetFiles(AudioPath, "*.wav"));
    }

    [Fact]
    public void AudioWriteFailureStillSavesTextWithWarning()
    {
        File.WriteAllText(AudioPath, "blocked directory");
        var service = Service();
        var result = Add(service);
        Assert.True(result.Saved);
        Assert.Null(result.Record.AudioFileName);
        Assert.NotNull(result.Warning);
        Assert.Single(service.Records);
        Assert.Equal("blocked directory", File.ReadAllText(AudioPath));
    }

    [Fact]
    public void FailedHistoryCommitCleansPreparedAudioWithoutChangingPriorHistory()
    {
        var service = Service();
        Assert.True(service.TryAddRecord(Record("existing")));
        var original = File.ReadAllBytes(HistoryPath);
        File.Move(HistoryPath, HistoryPath + ".original");
        Directory.CreateDirectory(HistoryPath);
        var result = Add(service);
        Assert.False(result.Saved);
        Assert.Null(result.Record.AudioFileName);
        Assert.Equal("existing", Assert.Single(service.Records).Id);
        Assert.Empty(Directory.GetFiles(AudioPath, "*.wav"));
        Assert.Equal(original, File.ReadAllBytes(HistoryPath + ".original"));
    }

    [Fact]
    public void FailedDeleteSaveRetainsAudioAndRestartClearsOnlyAbortedDeleteIntent()
    {
        var service = Service();
        var record = Add(service).Record;
        var path = service.ResolveAudioPath(record.AudioFileName)!;
        File.Move(HistoryPath, HistoryPath + ".original");
        Directory.CreateDirectory(HistoryPath);
        Assert.False(service.TryDeleteRecords([record.Id]));
        Assert.True(File.Exists(path));
        Directory.Delete(HistoryPath);
        File.Move(HistoryPath + ".original", HistoryPath);
        var restarted = Service();
        Assert.Single(restarted.Records);
        Assert.Null(restarted.AudioCleanupError);
        Assert.Equal(path, restarted.ResolveAudioPath(record.AudioFileName));
    }

    [Fact]
    public void SharedAudioSurvivesSingleDeleteAndRetentionUntilLastReferenceIsRemoved()
    {
        var service = Service();
        var first = Add(service).Record;
        var path = service.ResolveAudioPath(first.AudioFileName)!;
        Assert.True(service.TryReplaceRecord(first with { CreatedAt = DateTime.UtcNow.AddDays(-10) }));
        Assert.True(service.TryAddRecord(first with { Id = "shared", CreatedAt = DateTime.UtcNow }));
        service.PurgeOldRecords(TimeSpan.FromDays(1));
        Assert.Equal("shared", Assert.Single(service.Records).Id);
        Assert.True(File.Exists(path));
        service.ClearAll();
        Assert.Empty(service.Records);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ChangedAudioIsNotDeletedAndCleanupCanRetryAfterOriginalBytesReturn()
    {
        var service = Service();
        var record = Add(service).Record;
        var path = service.ResolveAudioPath(record.AudioFileName)!;
        var original = File.ReadAllBytes(path);
        File.WriteAllText(path, "foreign replacement");
        Assert.Null(service.ResolveAudioPath(record.AudioFileName));
        Assert.True(service.TryDeleteRecords([record.Id]));
        Assert.NotNull(service.AudioCleanupError);
        Assert.Equal("foreign replacement", File.ReadAllText(path));
        File.WriteAllBytes(path, original);
        Assert.Null(service.RetryAudioCleanup());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RestartReconcilesOnlyJournaledPreparationAndLeavesUnreferencedCommittedFiles()
    {
        var service = Service();
        var pending = Add(service, "pending").Record;
        var preserved = Add(service, "preserved").Record;
        var pendingPath = service.ResolveAudioPath(pending.AudioFileName)!;
        var preservedPath = service.ResolveAudioPath(preserved.AudioFileName)!;
        Assert.True(service.TryReplaceAll([])); // Restore deliberately does not remove audio.
        var indexPath = Path.Combine(AudioPath, "index.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        index["Pending"]!.AsArray().Add(pending.AudioFileName);
        File.WriteAllText(indexPath, index.ToJsonString());
        var unrelated = Path.Combine(AudioPath, "unrelated.wav");
        File.WriteAllText(unrelated, "unrelated");
        Assert.Empty(Service().Records);
        Assert.False(File.Exists(pendingPath));
        Assert.True(File.Exists(preservedPath));
        Assert.Equal("unrelated", File.ReadAllText(unrelated));
    }

    [Fact]
    public void MalformedHistoryCannotCausePendingAudioToBeTreatedAsUnreferenced()
    {
        var service = Service();
        var record = Add(service).Record;
        var path = service.ResolveAudioPath(record.AudioFileName)!;
        File.WriteAllText(HistoryPath, "broken");
        Assert.Throws<System.Text.Json.JsonException>(() => _ = Service().Records);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("outside.wav")]
    public void UnknownReferencesCannotDeleteExternalOrUnownedFiles(string name)
    {
        var service = Service();
        var outside = Path.Combine(_root, "outside.wav");
        File.WriteAllText(outside, "keep");
        Assert.True(service.TryAddRecord(Record() with { AudioFileName = name }));
        Assert.False(service.TryDeleteRecords(["one"]));
        Assert.NotNull(service.AudioCleanupError);
        Assert.Null(service.ResolveAudioPath(name));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Owned\":{},\"Pending\":[]}")]
    [InlineData("{\"Version\":1,\"version\":1,\"Owned\":{},\"Pending\":[]}")]
    [InlineData("{\"Version\":1,\"Owned\":{},\"Pending\":[],\"Extra\":true}")]
    public void InvalidOwnershipIndexIsPreservedAndAudioFallsBackToText(string json)
    {
        Directory.CreateDirectory(AudioPath);
        var index = Path.Combine(AudioPath, "index.json");
        File.WriteAllText(index, json);
        var result = Add(Service());
        Assert.True(result.Saved);
        Assert.Null(result.Record.AudioFileName);
        Assert.NotNull(result.Warning);
        Assert.Equal(json, File.ReadAllText(index));
    }

    [Fact]
    public void InvalidPcmFallsBackToTextAndWhitespaceLegacyReferenceIsNotAnAsset()
    {
        var service = Service();
        var result = service.TryAddRecordWithAudio(Record(), [float.NaN], 16000, () => true);
        Assert.True(result.Saved);
        Assert.NotNull(result.Warning);
        Assert.Null(result.Record.AudioFileName);
        Assert.True(service.TryAddRecord(Record("legacy") with { AudioFileName = " " }));
        Assert.True(service.TryDeleteRecords(["legacy"]));
    }

    [Fact]
    public void ReplacingAudioReferenceJournalsOldOwnedAudioButRestoreDoesNot()
    {
        var service = Service();
        var record = Add(service).Record;
        var path = service.ResolveAudioPath(record.AudioFileName)!;
        Assert.True(service.TryReplaceRecord(record with { AudioFileName = null }));
        Assert.False(File.Exists(path));
        var second = Add(service, "second").Record;
        var secondPath = service.ResolveAudioPath(second.AudioFileName)!;
        Assert.True(service.TryReplaceAll([]));
        Assert.Null(service.RetryAudioCleanup());
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task AddDuringPreparationIsSerializedAndSurvivesSnapshotDelete()
    {
        var service = Service();
        using var prepared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var adding = Task.Run(() => service.TryAddRecordWithAudio(Record(), Samples, 16000, () =>
        {
            if (++calls == 2)
            {
                prepared.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
            }
            return true;
        }));
        Assert.True(prepared.Wait(TimeSpan.FromSeconds(10)));
        Task<bool>? concurrent = null;
        try { concurrent = Task.Run(() => service.TryAddRecord(Record("later"))); }
        finally { release.Set(); }
        Assert.True((await adding).Saved);
        Assert.True(await concurrent!);
        Assert.True(service.TryDeleteRecords(["one"]));
        Assert.Equal("later", Assert.Single(service.Records).Id);
    }

    [Fact]
    public void LinkedAudioCannotBeResolvedOrDeleted()
    {
        var service = Service();
        var record = Add(service).Record;
        var path = service.ResolveAudioPath(record.AudioFileName)!;
        var target = Path.Combine(_root, "external.wav");
        File.Copy(path, target);
        File.Delete(path);
        try
        {
            try { File.CreateSymbolicLink(path, target); }
            catch (Exception ex) when (OperatingSystem.IsWindows() &&
                (ex is UnauthorizedAccessException or PlatformNotSupportedException ||
                 ex is IOException io && (io.HResult & 0xffff) == 1314))
            { return; } // Windows without Developer Mode/symlink privilege; Linux always executes.
            Assert.Null(service.ResolveAudioPath(record.AudioFileName));
            Assert.True(service.TryDeleteRecords([record.Id]));
            Assert.NotNull(service.AudioCleanupError);
            Assert.Equal(WavEncoder.Encode(Samples), File.ReadAllBytes(target));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path); // Remove this test's link, never its target.
        }
    }

    [Fact]
    public void TamperedPendingFileDoesNotBlockIndependentOwnedCleanup()
    {
        var service = Service();
        var first = Add(service, "first").Record;
        var second = Add(service, "second").Record;
        var firstPath = service.ResolveAudioPath(first.AudioFileName)!;
        var secondPath = service.ResolveAudioPath(second.AudioFileName)!;
        var original = File.ReadAllBytes(firstPath);
        Assert.True(service.TryReplaceAll([]));
        File.WriteAllText(firstPath, "unverified replacement");
        var indexPath = Path.Combine(AudioPath, "index.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        index["Pending"]!.AsArray().Add(first.AudioFileName);
        index["Pending"]!.AsArray().Add(second.AudioFileName);
        File.WriteAllText(indexPath, index.ToJsonString());

        Assert.NotNull(service.RetryAudioCleanup());
        Assert.Equal("unverified replacement", File.ReadAllText(firstPath));
        Assert.False(File.Exists(secondPath));
        var persisted = JsonNode.Parse(File.ReadAllText(indexPath))!;
        Assert.Equal(first.AudioFileName, Assert.Single(persisted["Pending"]!.AsArray())!.GetValue<string>());
        Assert.False(persisted["Owned"]!.AsObject().ContainsKey(second.AudioFileName!));
        File.WriteAllBytes(firstPath, original);
        Assert.Null(service.RetryAudioCleanup());
        Assert.False(File.Exists(firstPath));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
