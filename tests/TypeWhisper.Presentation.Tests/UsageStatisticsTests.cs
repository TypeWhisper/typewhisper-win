using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class UsageStatisticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static TranscriptionRecord Entry(string id, DateTime timestamp, string text, double seconds = 60,
        string? app = null, string? model = null) => new()
    {
        Id = id, Timestamp = timestamp, CreatedAt = timestamp, RawText = "preserved raw text", FinalText = text,
        DurationSeconds = seconds, AppName = app, ModelUsed = model, EngineUsed = "provider"
    };

    [Fact]
    public void UsesDisplayedTextStoredDurationAndOnlyKnownAttribution()
    {
        var records = new[]
        {
            Entry("one", Now.UtcDateTime.AddHours(-1), "two\twords\nplus", 120, "Editor", "model"),
            Entry("two", Now.UtcDateTime.AddHours(-2), "", 60) with { AppProcessName = "editor" },
            Entry("three", Now.UtcDateTime.AddHours(-3), "last", double.NaN),
            Entry("future", Now.UtcDateTime.AddSeconds(1), "do not count", 900, "Future", "future")
        };
        var result = new UsageStatistics(records, Now, TimeZoneInfo.Utc).Summarize(UsagePeriod.AllTime);
        Assert.Equal(7, result.Words);
        Assert.Equal(3, result.Transcriptions);
        Assert.Equal(3, result.Minutes);
        Assert.Equal(1, result.ActiveDays);
        Assert.Equal(1, result.KnownApps);
        Assert.Equal(1, result.KnownModels);
        Assert.Equal(2, result.Apps.Single(rank => rank.Name == "Editor").Count);
        Assert.Equal(1, result.Apps.Single(rank => rank.Name == "App not recorded").Count);
        Assert.Equal(2, result.Models.Single(rank => rank.Name == "Model not recorded").Count);
    }

    [Fact]
    public void InclusiveRangesUseLocalCalendarDateAndHour()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture UTC+2", TimeSpan.FromHours(2), "Fixture", "Fixture");
        var records = new[]
        {
            Entry("before", new DateTime(2026, 9, 6, 21, 59, 59, DateTimeKind.Utc), "before"),
            Entry("midnight", new DateTime(2026, 9, 6, 22, 0, 0, DateTimeKind.Utc), "midnight local")
        };
        var result = new UsageStatistics(records, Now, zone).SummarizeRange(new(2026, 9, 7), new(2026, 9, 7));
        Assert.Equal(1, result.Transcriptions);
        Assert.Equal(2, result.Words);
        Assert.Equal(1, result.Hours[0, 0]); // Monday at local midnight.
        Assert.Equal(new DateOnly(2026, 9, 7), Assert.Single(result.Days).Date);
    }

    [Fact]
    public void WeekIncludesExactlySevenDatesAndEmptyRangeHasZeroFilledDays()
    {
        var records = new[]
        {
            Entry("inside", Now.UtcDateTime.AddDays(-6), "in"),
            Entry("outside", Now.UtcDateTime.AddDays(-7), "out")
        };
        var statistics = new UsageStatistics(records, Now, TimeZoneInfo.Utc);
        var week = statistics.Summarize(UsagePeriod.Week);
        Assert.Equal(1, week.Transcriptions);
        Assert.Equal(7, week.Days.Length);
        var empty = statistics.SummarizeRange(new(2026, 8, 1), new(2026, 8, 3));
        Assert.Equal(0, empty.Transcriptions);
        Assert.Equal(0, empty.Minutes);
        Assert.Equal(3, empty.Days.Length);
        Assert.All(empty.Days, day => Assert.Equal(0, day.Words));
        Assert.Empty(empty.Apps);
        Assert.Empty(empty.Models);
        Assert.All(empty.Hours.Cast<int>(), count => Assert.Equal(0, count));
    }

    [Fact]
    public async Task RefreshAfterEditDeleteAndRestartRecomputesInsteadOfAccumulating()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typewhisper-usage-" + Guid.NewGuid());
        var path = Path.Combine(directory, "history.json");
        try
        {
            Directory.CreateDirectory(directory);
            var history = new HistoryService(path) { ThrowOnLoadFailure = true };
            Assert.True(history.TryAddRecord(Entry("entry", Now.UtcDateTime.AddHours(-1), "one two", 120)));
            var reader = new HistoryReader(history);
            var initial = new UsageStatistics(await reader.ReadAsync(), Now, TimeZoneInfo.Utc);
            Assert.Equal(2, initial.Summarize(UsagePeriod.AllTime).Words);
            Assert.NotNull(await new HistoryActions(history).EditAsync("entry", "one two three"));
            var edited = new UsageStatistics(await reader.ReadAsync(), Now, TimeZoneInfo.Utc).Summarize(UsagePeriod.AllTime);
            Assert.Equal(3, edited.Words);
            Assert.Equal(2, edited.Minutes);
            Assert.Equal(2, initial.Summarize(UsagePeriod.AllTime).Words); // Captured snapshot stays unchanged.
            var restarted = new HistoryService(path) { ThrowOnLoadFailure = true };
            Assert.True(await new HistoryActions(restarted).DeleteAsync("entry"));
            var deleted = new UsageStatistics(await new HistoryReader(restarted).ReadAsync(), Now, TimeZoneInfo.Utc).Summarize(UsagePeriod.AllTime);
            Assert.Equal(0, deleted.Transcriptions);
            Assert.Equal(0, deleted.Words);
            Assert.Equal(0, deleted.Minutes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidCustomRangesFailAndNonfiniteOrNegativeDurationsDoNotInflateTotals()
    {
        var statistics = new UsageStatistics([
            Entry("negative", Now.UtcDateTime, "text", -5),
            Entry("infinite", Now.UtcDateTime, "text", double.PositiveInfinity)
        ], Now, TimeZoneInfo.Utc);
        Assert.Equal(0, statistics.Summarize(UsagePeriod.AllTime).Minutes);
        Assert.Throws<ArgumentException>(() => statistics.SummarizeRange(new(2026, 9, 8), new(2026, 9, 7)));
        Assert.NotNull(UsageStatistics.ValidateRange(new(1900, 1, 1), new(2026, 1, 1)));
    }
}
