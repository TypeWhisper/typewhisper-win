using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class HistoryTimestampTests
{
    [Theory]
    [InlineData(2026, 1, 15, 10, 11)]
    [InlineData(2026, 7, 15, 10, 12)]
    public void ConvertUtcToLocalTime_AppliesBerlinDaylightSavingOffset(
        int year,
        int month,
        int day,
        int utcHour,
        int expectedLocalHour)
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var utc = new DateTime(year, month, day, utcHour, 0, 0, DateTimeKind.Utc);

        var local = HistoryEntryViewModel.ConvertUtcToLocalTime(utc, berlin);

        Assert.Equal(expectedLocalHour, local.Hour);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }

    [Theory]
    [InlineData(2026, 3, 29, 0, 30, 1, 30)]
    [InlineData(2026, 3, 29, 1, 30, 3, 30)]
    [InlineData(2026, 10, 25, 0, 30, 2, 30)]
    [InlineData(2026, 10, 25, 1, 30, 2, 30)]
    public void ConvertUtcToLocalTime_HandlesBerlinDaylightSavingTransitions(
        int year,
        int month,
        int day,
        int utcHour,
        int utcMinute,
        int expectedLocalHour,
        int expectedLocalMinute)
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var utc = new DateTime(year, month, day, utcHour, utcMinute, 0, DateTimeKind.Utc);

        var local = HistoryEntryViewModel.ConvertUtcToLocalTime(utc, berlin);

        Assert.Equal(expectedLocalHour, local.Hour);
        Assert.Equal(expectedLocalMinute, local.Minute);
    }

    [Fact]
    public void ConvertUtcToLocalTime_TreatsPersistedUnspecifiedTimestampAsUtc()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var persistedUtc = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Unspecified);

        var local = HistoryEntryViewModel.ConvertUtcToLocalTime(persistedUtc, berlin);

        Assert.Equal(new DateTime(2026, 7, 15, 12, 0, 0), local);
    }
}
