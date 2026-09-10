using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Calendar ranges for statistics derived from retained history.</summary>
public enum UsagePeriod
{
    /// <summary>Today and the preceding six local dates.</summary>
    Week,
    /// <summary>Today and the preceding twenty-nine local dates.</summary>
    Month,
    /// <summary>All retained entries through the current time.</summary>
    AllTime,
    /// <summary>An explicitly selected inclusive calendar range.</summary>
    Custom
}
/// <summary>Words in retained entries on one local calendar date.</summary>
public sealed record UsageDay(DateOnly Date, int Words);
/// <summary>The number of retained entries attributed to an app or model.</summary>
public sealed record UsageRank(string Name, int Count);
/// <summary>A read-only aggregation; deletion and retention reduce these figures.</summary>
public sealed record UsageSummary(int Words, double Minutes, int Transcriptions, int ActiveDays,
    int KnownApps, int KnownModels, UsageDay[] Days, UsageRank[] Apps, UsageRank[] Models, int[,] Hours);

/// <summary>Computes statistics from a history snapshot without accumulating lifetime counters.</summary>
public sealed class UsageStatistics
{
    private sealed record Entry(DateTime At, int Words, double Minutes, string? App, string? Model);
    private readonly Entry[] _entries;
    private readonly DateOnly _today;

    /// <summary>Converts persisted UTC timestamps to the requested display timezone.</summary>
    public UsageStatistics(IEnumerable<TranscriptionRecord> records, DateTimeOffset? now = null, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var instant = now ?? DateTimeOffset.UtcNow;
        _today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        _entries = records.Where(record => DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc) <= instant.UtcDateTime)
            .Select(record => new Entry(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc), zone),
                record.DisplayText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                double.IsFinite(record.DurationSeconds) ? Math.Max(0, record.DurationSeconds) / 60 : 0,
                Clean(record.AppName) ?? Clean(record.AppProcessName),
                Clean(record.ModelUsed) is { } model ? (Clean(record.EngineUsed) is { } engine ? engine + " · " + model : model) : null))
            .ToArray();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Summarizes a trailing week, trailing month, or all retained history through today.</summary>
    public UsageSummary Summarize(UsagePeriod period)
    {
        var start = period switch
        {
            UsagePeriod.Week => _today.AddDays(-6),
            UsagePeriod.Month => _today.AddDays(-29),
            _ => _entries.Length == 0 ? _today : _entries.Min(entry => DateOnly.FromDateTime(entry.At))
        };
        return Build(start, _today);
    }

    /// <summary>Validates an inclusive custom calendar range.</summary>
    public static string? ValidateRange(DateOnly start, DateOnly end)
    {
        if (start.Year < 1900 || end.Year > 2100) return "Choose dates between 1900 and 2100.";
        if (start > end) return "The start date must be on or before the end date.";
        if (end.DayNumber - start.DayNumber > 3659) return "Choose a range of up to 10 years.";
        return null;
    }

    /// <summary>Summarizes an inclusive local date range.</summary>
    public UsageSummary SummarizeRange(DateOnly start, DateOnly end)
    {
        if (ValidateRange(start, end) is { } error) throw new ArgumentException(error);
        return Build(start, end);
    }

    private UsageSummary Build(DateOnly start, DateOnly end)
    {
        var entries = _entries.Where(entry => DateOnly.FromDateTime(entry.At) >= start && DateOnly.FromDateTime(entry.At) <= end).ToArray();
        var byDay = entries.GroupBy(entry => DateOnly.FromDateTime(entry.At)).ToDictionary(group => group.Key, group => group.Sum(entry => entry.Words));
        var days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1)
            .Select(offset => start.AddDays(offset)).Select(day => new UsageDay(day, byDay.GetValueOrDefault(day))).ToArray();
        UsageRank[] Rank(Func<Entry, string?> key, string unknown) => entries.GroupBy(entry => key(entry) ?? unknown, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsageRank(group.Key, group.Count())).OrderByDescending(rank => rank.Count)
            .ThenBy(rank => rank.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var hours = new int[7, 24];
        foreach (var entry in entries) hours[((int)entry.At.DayOfWeek + 6) % 7, entry.At.Hour]++;
        return new UsageSummary(entries.Sum(entry => entry.Words), entries.Sum(entry => entry.Minutes), entries.Length, byDay.Count,
            entries.Where(entry => entry.App is not null).Select(entry => entry.App).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            entries.Where(entry => entry.Model is not null).Select(entry => entry.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            days, Rank(entry => entry.App, "App not recorded"), Rank(entry => entry.Model, "Model not recorded"), hours);
    }
}
