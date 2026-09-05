namespace TypeWhisper.WinUI;

internal enum PrototypeUsagePeriod { Week, Month, AllTime, Custom }
internal sealed record PrototypeUsageEvent(DateTime At, int Words, double Minutes, string App, string Model);
internal sealed record PrototypeUsageDay(DateOnly Date, int Words);
internal sealed record PrototypeUsageRank(string Name, int Count);

// Disposable usage fixtures, deliberately independent of history retention as on macOS.
internal sealed class PrototypeUsageData
{
    internal DateOnly Today { get; } = new(2026, 9, 5);
    internal IReadOnlyList<PrototypeUsageEvent> Events { get; }
    internal PrototypeUsageData(bool empty = false)
    {
        var events = new List<PrototypeUsageEvent>();
        if (!empty)
            for (var day = 0; day < 56; day++)
            {
                if (day % 13 == 3 || day % 17 == 6) continue;
                for (var session = 0; session < 3 + day * 7 % 26; session++)
                {
                    var words = 12 + (day * 31 + session * 17) % 150;
                    events.Add(new(Today.AddDays(-day).ToDateTime(new TimeOnly(8 + session % 13, session * 7 % 60)),
                        words, words / (105d + session % 35),
                        new[] { "Visual Studio Code", "Outlook", "Microsoft Edge", "Notepad", "Teams", "Obsidian" }[(session + day) % 6],
                        new[] { "Parakeet · TDT v3", "Whisper · Large v3 Turbo", "Qwen3 ASR · 1.7B", "Whisper · Small" }[(session / 3 + day) % 4]));
                }
            }
        Events = events.AsReadOnly();
    }
    internal PrototypeUsageSummary Summarize(PrototypeUsagePeriod period) => Summarize(Events, Today, period);
    internal static PrototypeUsageSummary Summarize(IEnumerable<PrototypeUsageEvent> source, DateOnly today, PrototypeUsagePeriod period)
    {
        var all = source.Where(item => DateOnly.FromDateTime(item.At) <= today).ToArray();
        var start = period switch { PrototypeUsagePeriod.Week => today.AddDays(-6), PrototypeUsagePeriod.Month => today.AddDays(-29), _ => all.Length == 0 ? today : all.Min(item => DateOnly.FromDateTime(item.At)) };
        return SummarizeRange(all, start, today);
    }
    internal static string? ValidateRange(DateOnly start, DateOnly end)
    {
        if (start.Year < 1900 || end.Year > 2100) return "Choose dates between 1900 and 2100.";
        if (start > end) return "The start date must be on or before the end date.";
        if (end.DayNumber - start.DayNumber > 3659) return "Choose a range of up to 10 years in this preview.";
        return null;
    }
    internal static PrototypeUsageSummary SummarizeRange(IEnumerable<PrototypeUsageEvent> source, DateOnly start, DateOnly end)
    {
        if (ValidateRange(start, end) is { } error) throw new ArgumentException(error);
        var events = source.Where(item => DateOnly.FromDateTime(item.At) >= start && DateOnly.FromDateTime(item.At) <= end).ToArray();
        var active = events.Select(item => DateOnly.FromDateTime(item.At)).Distinct().Order().ToArray();
        var longest = 0; var run = 0; DateOnly? previous = null;
        foreach (var date in active) { run = previous?.AddDays(1) == date ? run + 1 : 1; longest = Math.Max(longest, run); previous = date; }
        var current = 0; var last = active.LastOrDefault();
        if (active.Length > 0 && last >= end.AddDays(-1))
            for (var date = last; active.Contains(date); date = date.AddDays(-1)) current++;
        var days = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(offset => start.AddDays(offset))
            .Select(date => new PrototypeUsageDay(date, events.Where(item => DateOnly.FromDateTime(item.At) == date).Sum(item => item.Words))).ToArray();
        PrototypeUsageRank[] Rank(Func<PrototypeUsageEvent, string> key) => events.GroupBy(key).Select(group => new PrototypeUsageRank(group.Key, group.Count())).OrderByDescending(item => item.Count).ThenBy(item => item.Name).ToArray();
        var hours = new int[7, 24]; foreach (var item in events) hours[((int)item.At.DayOfWeek + 6) % 7, item.At.Hour]++;
        return new(events.Sum(item => item.Words), events.Sum(item => item.Minutes), events.Length, active.Length, current, longest, days, Rank(item => item.App), Rank(item => item.Model), hours);
    }
}

internal sealed record PrototypeUsageSummary(int Words, double Minutes, int Transcriptions, int ActiveDays,
    int CurrentStreak, int LongestStreak, PrototypeUsageDay[] Days, PrototypeUsageRank[] Apps, PrototypeUsageRank[] Models, int[,] Hours)
{
    internal int Wpm => Minutes > 0 ? (int)(Words / Minutes) : 0;
    // Explicit prototype estimate; not a claim about measured personal productivity.
    internal int SavedMinutes => (int)Math.Max(0, Words / 40d - Minutes);
    internal string SavedLabel => SavedMinutes >= 60 ? $"{SavedMinutes / 60}h {SavedMinutes % 60}m" : $"{SavedMinutes}m";
}
