using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class UsageStatisticsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "TypeWhisper-UsageStatisticsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecordTranscription_AggregatesDayAppModelAndHourAndPersists()
    {
        var path = Path.Combine(_directory, "usage-statistics.json");
        var timestamp = DateTime.SpecifyKind(new DateTime(2026, 8, 8, 14, 30, 0), DateTimeKind.Local);
        var service = new UsageStatisticsService(path);

        service.RecordTranscription(timestamp, 120, 60, "notepad", "Notes", "whisper", "large-v3");
        service.RecordTranscription(timestamp.AddMinutes(15), 30, 15, "notepad", "Notes", "whisper", "large-v3");

        var day = Assert.Single(service.Days);
        Assert.Equal(timestamp.Date, day.Day);
        Assert.Equal(2, day.TranscriptionCount);
        Assert.Equal(150, day.TotalWords);
        Assert.Equal(75, day.TotalDurationSeconds);
        Assert.Equal(2, day.AppCounts["notepad"]);
        Assert.Equal(2, day.ModelCounts[UsageStatisticsService.BuildModelKey("whisper", "large-v3")]);
        Assert.Equal(2, day.HourCounts[14]);

        var reloaded = new UsageStatisticsService(path);
        var reloadedDay = Assert.Single(reloaded.Days);
        Assert.Equal(day.Day, reloadedDay.Day);
        Assert.Equal(day.TranscriptionCount, reloadedDay.TranscriptionCount);
        Assert.Equal(day.TotalWords, reloadedDay.TotalWords);
        Assert.Equal(day.AppCounts, reloadedDay.AppCounts);
        Assert.Equal(day.ModelCounts, reloadedDay.ModelCounts);
        Assert.Equal(day.HourCounts, reloadedDay.HourCounts);
    }

    [Fact]
    public void BackfillFromHistoryIfNeeded_ImportsOnlyOnceAndSurvivesHistoryChanges()
    {
        var path = Path.Combine(_directory, "usage-statistics.json");
        var timestamp = DateTime.SpecifyKind(new DateTime(2026, 8, 7, 9, 0, 0), DateTimeKind.Local);
        var record = CreateRecord("one", timestamp, "hello persistent statistics");
        var service = new UsageStatisticsService(path);

        service.BackfillFromHistoryIfNeeded([record]);
        service.BackfillFromHistoryIfNeeded([record]);

        var day = Assert.Single(service.Days);
        Assert.Equal(1, day.TranscriptionCount);
        Assert.Equal(3, day.TotalWords);

        var reloaded = new UsageStatisticsService(path);
        reloaded.BackfillFromHistoryIfNeeded([]);
        var reloadedDay = Assert.Single(reloaded.Days);
        Assert.Equal(day.Day, reloadedDay.Day);
        Assert.Equal(day.TranscriptionCount, reloadedDay.TranscriptionCount);
        Assert.Equal(day.TotalWords, reloadedDay.TotalWords);
    }

    [Fact]
    public void ReplaceWithHistoryRecords_DropsPreviousAggregates()
    {
        var path = Path.Combine(_directory, "usage-statistics.json");
        var service = new UsageStatisticsService(path);
        var timestamp = DateTime.SpecifyKind(new DateTime(2026, 8, 6, 11, 0, 0), DateTimeKind.Local);
        service.RecordTranscription(timestamp, 10, 5, "old", null, "old", null);

        service.ReplaceWithHistoryRecords([
            CreateRecord("replacement", timestamp.AddDays(1), "replacement text")
        ]);

        var day = Assert.Single(service.Days);
        Assert.Equal(timestamp.AddDays(1).Date, day.Day);
        Assert.Equal(2, day.TotalWords);
        Assert.DoesNotContain("old", day.AppCounts.Keys);
    }

    private static TranscriptionRecord CreateRecord(string id, DateTime timestamp, string text) => new()
    {
        Id = id,
        Timestamp = timestamp,
        RawText = text,
        FinalText = text,
        AppProcessName = "notepad",
        DurationSeconds = 30,
        EngineUsed = "whisper",
        ModelUsed = "large-v3"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
