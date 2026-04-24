using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class RecentTranscriptionStoreTests
{
    [Fact]
    public void MergedEntries_SortsNewestFirstAndDedupesById()
    {
        var sut = new RecentTranscriptionStore();
        var now = DateTime.UtcNow;
        var duplicateId = Guid.NewGuid().ToString();

        sut.RecordTranscription(
            duplicateId,
            "Session duplicate",
            now.AddMinutes(-2),
            "Mail",
            "mail");
        sut.RecordTranscription(
            "session-newest",
            "Session newest",
            now,
            "Notes",
            "notepad");

        var history = new[]
        {
            CreateRecord(duplicateId, "History duplicate wins", now.AddMinutes(-1)),
            CreateRecord("history-oldest", "History oldest", now.AddMinutes(-3)),
        };

        var merged = sut.MergedEntries(history);

        Assert.Equal(
            new[] { "Session newest", "History duplicate wins", "History oldest" },
            merged.Select(entry => entry.FinalText));
    }

    [Fact]
    public void MergedEntries_LimitsResults()
    {
        var sut = new RecentTranscriptionStore();
        var now = DateTime.UtcNow;

        for (var index = 0; index < 20; index++)
        {
            sut.RecordTranscription(
                $"session-{index}",
                $"Entry {index}",
                now.AddSeconds(index),
                null,
                null);
        }

        var merged = sut.MergedEntries([], limit: 12);

        Assert.Equal(12, merged.Count);
        Assert.Equal("Entry 19", merged[0].FinalText);
        Assert.Equal("Entry 8", merged[^1].FinalText);
    }

    [Fact]
    public void RecordTranscription_IgnoresBlankText()
    {
        var sut = new RecentTranscriptionStore();

        sut.RecordTranscription("blank", "   ", DateTime.UtcNow, null, null);

        Assert.Empty(sut.SessionEntries);
        Assert.Empty(sut.MergedEntries([]));
    }

    [Fact]
    public void LatestEntry_FallsBackToSessionEntriesWhenHistoryIsEmpty()
    {
        var sut = new RecentTranscriptionStore();
        var id = Guid.NewGuid().ToString();

        sut.RecordTranscription(id, "Session fallback", DateTime.UtcNow, "Slack", "slack");

        var latest = sut.LatestEntry([]);

        Assert.NotNull(latest);
        Assert.Equal(id, latest!.Id);
        Assert.Equal(RecentTranscriptionSource.Session, latest.Source);
    }

    private static TranscriptionRecord CreateRecord(string id, string finalText, DateTime timestamp) =>
        new()
        {
            Id = id,
            Timestamp = timestamp,
            CreatedAt = timestamp,
            RawText = finalText,
            FinalText = finalText
        };
}
