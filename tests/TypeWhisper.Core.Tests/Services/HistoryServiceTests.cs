using TypeWhisper.Core.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class HistoryServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TypeWhisperDatabase _db;
    private readonly HistoryService _sut;

    public HistoryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tw_test_{Guid.NewGuid():N}.db");
        _db = new TypeWhisperDatabase(_dbPath);
        _db.Initialize();
        _sut = new HistoryService(_db);
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

        var freshService = new HistoryService(_db);
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

        var freshService = new HistoryService(_db);
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

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
