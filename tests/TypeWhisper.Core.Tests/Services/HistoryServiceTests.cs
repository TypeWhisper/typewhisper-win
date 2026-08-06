using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class HistoryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly string _audioDirectory;
    private readonly HistoryService _sut;

    public HistoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_history_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "history.json");
        _audioDirectory = Path.Combine(_tempDir, "audio");
        Directory.CreateDirectory(_audioDirectory);
        _sut = new HistoryService(_filePath, _audioDirectory);
    }

    [Fact]
    public void ModelUsed_PersistsCorrectly()
    {
        var record = new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            RawText = "hello",
            FinalText = "hello",
            EngineUsed = "plugin:com.test:model-1",
            ModelUsed = "plugin:com.test:model-1"
        };

        _sut.AddRecord(record);

        var freshService = new HistoryService(_filePath);
        var loaded = freshService.Records.First(r => r.Id == record.Id);
        Assert.Equal("plugin:com.test:model-1", loaded.ModelUsed);
    }

    [Fact]
    public void ModelUsed_NullByDefault()
    {
        var record = new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            RawText = "test",
            FinalText = "test"
        };

        _sut.AddRecord(record);

        var freshService = new HistoryService(_filePath);
        var loaded = freshService.Records.First(r => r.Id == record.Id);
        Assert.Null(loaded.ModelUsed);
    }

    [Fact]
    public void ExportToMarkdown_FormatsCorrectly()
    {
        var records = new List<TranscriptionRecord>
        {
            new()
            {
                Id = "1",
                Timestamp = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc),
                RawText = "hello world",
                FinalText = "Hello, world!",
                AppProcessName = "notepad",
                DurationSeconds = 2.5,
                Language = "en"
            }
        };

        var result = _sut.ExportToMarkdown(records);

        Assert.Contains("# TypeWhisper", result);
        Assert.Contains("Hello, world!", result);
        Assert.Contains("notepad", result);
        Assert.Contains("2.5", result);
    }

    [Fact]
    public void ExportToJson_ProducesValidJson()
    {
        var records = new List<TranscriptionRecord>
        {
            new()
            {
                Id = "1",
                Timestamp = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc),
                RawText = "hello",
                FinalText = "Hello!",
                AppProcessName = "code",
                DurationSeconds = 1.0,
                Language = "en"
            }
        };

        var result = _sut.ExportToJson(records);

        Assert.Contains("\"text\"", result);
        Assert.Contains("Hello!", result);
        Assert.StartsWith("[", result.Trim());
        Assert.EndsWith("]", result.Trim());
    }

    [Fact]
    public void PurgeOldRecords_RemovesEntriesOlderThanRetention()
    {
        var now = DateTime.UtcNow;
        var stale = CreateRecord("stale", now.AddHours(-2));
        var fresh = CreateRecord("fresh", now.AddMinutes(-20));

        _sut.AddRecord(stale);
        _sut.AddRecord(fresh);

        _sut.PurgeOldRecords(TimeSpan.FromHours(1));

        Assert.DoesNotContain(_sut.Records, record => record.Id == stale.Id);
        Assert.Contains(_sut.Records, record => record.Id == fresh.Id);
    }

    [Fact]
    public void PurgeOldRecords_NullRetentionDoesNothing()
    {
        var record = CreateRecord("keep", DateTime.UtcNow.AddDays(-30));
        _sut.AddRecord(record);

        _sut.PurgeOldRecords(null);

        Assert.Contains(_sut.Records, entry => entry.Id == record.Id);
    }

    [Fact]
    public void PurgeOldRecords_DeletesAssociatedAudioFiles()
    {
        var audioFile = "expired.wav";
        File.WriteAllText(Path.Combine(_audioDirectory, audioFile), "audio");
        var record = CreateRecord("expired", DateTime.UtcNow.AddHours(-3), audioFile);
        _sut.AddRecord(record);

        _sut.PurgeOldRecords(TimeSpan.FromHours(1));

        Assert.False(File.Exists(Path.Combine(_audioDirectory, audioFile)));
    }

    [Fact]
    public void ClearAll_DeletesAssociatedAudioFiles()
    {
        var audioFile = "session.wav";
        File.WriteAllText(Path.Combine(_audioDirectory, audioFile), "audio");
        _sut.AddRecord(CreateRecord("session", DateTime.UtcNow, audioFile));

        _sut.ClearAll();

        Assert.Empty(_sut.Records);
        Assert.False(File.Exists(Path.Combine(_audioDirectory, audioFile)));
    }

    [Fact]
    public void LegacyRecordWithEmptyFinalText_UsesRawTextForDisplaySearchStatsAndExports()
    {
        const string rawText = "recover these original words";
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "Timestamp": "2026-08-05T10:00:00Z",
                "RawText": "recover these original words",
                "FinalText": ""
              }
            ]
            """);
        var legacyHistory = new HistoryService(_filePath);

        var loaded = Assert.Single(legacyHistory.Records);

        Assert.Equal(rawText, loaded.DisplayText);
        Assert.Equal(4, loaded.WordCount);
        Assert.Equal(rawText, loaded.Preview);
        Assert.Single(legacyHistory.Search("original"));
        Assert.Contains(rawText, legacyHistory.ExportToText([loaded]));
        Assert.Contains(rawText, legacyHistory.ExportToCsv([loaded]));
        Assert.Contains(rawText, legacyHistory.ExportToMarkdown([loaded]));
    }

    [Fact]
    public void TryReplaceRecord_AtomicallyReplacesFullRecordAndRebuildsStatistics()
    {
        var original = CreateRecord("same-id", DateTime.UtcNow);
        Assert.True(_sut.TryAddRecord(original));
        var replacement = original with
        {
            FinalText = "one two three four",
            Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed,
            WorkflowFailureMessage = "provider failed",
            WorkflowId = "workflow-id"
        };

        Assert.True(_sut.TryReplaceRecord(replacement));

        var current = Assert.Single(_sut.Records);
        Assert.Equal("same-id", current.Id);
        Assert.Equal(replacement.FinalText, current.FinalText);
        Assert.Equal(TranscriptionRecordStatus.WorkflowPostProcessingFailed, current.Status);
        Assert.Equal(4, _sut.TotalWords);
        Assert.Single(new HistoryService(_filePath).Records);
    }

    [Fact]
    public void FailedAtomicWrite_DoesNotMutateCacheOrStatistics()
    {
        var blockingParent = Path.Combine(_tempDir, "not-a-directory");
        File.WriteAllText(blockingParent, "file");
        var sut = new HistoryService(Path.Combine(blockingParent, "history.json"));
        var record = CreateRecord("not-saved", DateTime.UtcNow);

        var saved = sut.TryAddRecord(record);

        Assert.False(saved);
        Assert.Empty(sut.Records);
        Assert.Equal(0, sut.TotalRecords);
        Assert.Equal(0, sut.TotalWords);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static TranscriptionRecord CreateRecord(string id, DateTime createdAt, string? audioFileName = null) =>
        new()
        {
            Id = id,
            Timestamp = createdAt,
            CreatedAt = createdAt,
            RawText = "test",
            FinalText = "test",
            AudioFileName = audioFileName
        };
}
