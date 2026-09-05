using TypeWhisper.WinUIPrototype;
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var sample = new PrototypeUsageData();
var week = sample.Summarize(PrototypeUsagePeriod.Week);
var month = sample.Summarize(PrototypeUsagePeriod.Month);
var all = sample.Summarize(PrototypeUsagePeriod.AllTime);
Check(week.Days.Length == 7, "Seven inclusive days");
Check(month.Days.Length == 30, "Thirty inclusive days");
Check(all.Days.First().Date == DateOnly.FromDateTime(sample.Events.Min(item => item.At)), "All time starts at first activity");
Check(week.Days.Last().Date == sample.Today, "Includes today");
Check(week.Words < month.Words && month.Words < all.Words, "Period totals differ");
foreach (var data in new[] { week, month, all })
{
    Check(data.Days.Sum(day => day.Words) == data.Words, "Chart and word card agree");
    Check(data.Apps.Sum(app => app.Count) == data.Transcriptions, "Apps account for total");
    Check(data.Models.Sum(model => model.Count) == data.Transcriptions, "Models account for total");
    Check(data.Hours.Cast<int>().Sum() == data.Transcriptions, "Heatmap accounts for total");
    Check(data.SavedMinutes >= 0 && data.Wpm > 0, "Valid time and speed");
}
var empty = new PrototypeUsageData(true).Summarize(PrototypeUsagePeriod.Week);
Check(empty.Words == 0 && empty.Wpm == 0 && empty.SavedMinutes == 0, "Empty metrics");
Check(empty.CurrentStreak == 0 && empty.LongestStreak == 0 && empty.ActiveDays == 0, "Empty streaks");
Check(empty.Days.Length == 7 && empty.Days.All(day => day.Words == 0), "Zero-filled empty chart");
var today = sample.Today;
PrototypeUsageEvent Event(int daysAgo) => new(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(12, 0)), 100, 1, "App", "Model");
var streak = PrototypeUsageData.Summarize(new[] { Event(0), Event(1), Event(2), Event(6) }, today, PrototypeUsagePeriod.Week);
Check(streak.CurrentStreak == 3 && streak.LongestStreak == 3 && streak.ActiveDays == 4, "Gap breaks streak");
Check(PrototypeUsageData.Summarize(new[] { Event(1), Event(2) }, today, PrototypeUsagePeriod.Week).CurrentStreak == 2, "Yesterday keeps current streak");
Check(PrototypeUsageData.Summarize(new[] { Event(2) }, today, PrototypeUsagePeriod.Week).CurrentStreak == 0, "Older streak expires");
Check(PrototypeUsageData.Summarize(new[] { Event(-1), Event(7) }, today, PrototypeUsagePeriod.Week).Transcriptions == 0, "Exclude future and outside interval");
Check(PrototypeUsageData.Summarize(new[] { Event(0) with { Minutes = 10 } }, today, PrototypeUsagePeriod.AllTime).SavedMinutes == 0, "Never negative saving");
var single = PrototypeUsageData.SummarizeRange(new[] { Event(0), Event(0) with { At = today.ToDateTime(new TimeOnly(23, 59)) }, Event(1) }, today, today);
Check(single.Transcriptions == 2 && single.Days.Length == 1, "Single inclusive day includes late events");
var custom = PrototypeUsageData.SummarizeRange(sample.Events, today.AddDays(-10), today.AddDays(-3));
Check(custom.Days.Length == 8 && custom.Days.First().Date == today.AddDays(-10) && custom.Days.Last().Date == today.AddDays(-3), "Inclusive custom endpoints");
Check(custom.Words == custom.Days.Sum(day => day.Words), "Custom chart total");
Check(custom.Hours.Cast<int>().Sum() == custom.Transcriptions, "Custom heatmap total");
Check(PrototypeUsageData.ValidateRange(today, today.AddDays(-1)) is not null, "Reject reversed range");
Check(PrototypeUsageData.ValidateRange(today.AddYears(-11), today) is not null, "Bound preview range");
Check(PrototypeUsageData.ValidateRange(new(1899, 12, 31), new(1900, 1, 1)) is not null, "Calendar lower bound");
Check(PrototypeUsageData.SummarizeRange(sample.Events, new(2024, 2, 28), new(2024, 3, 1)).Days.Length == 3, "Leap day included");
Check(PrototypeUsageData.SummarizeRange(sample.Events, today.AddYears(-1), today.AddYears(-1)).Words == 0, "Empty custom day");
Console.WriteLine($"{checks} usage model checks passed.");
