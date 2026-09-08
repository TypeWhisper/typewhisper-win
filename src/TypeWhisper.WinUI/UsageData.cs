using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Statistics are a projection of currently retained history, never lifetime telemetry.
internal sealed class UsageData(IEnumerable<TranscriptionRecord> records)
{
    private readonly UsageStatistics _statistics = new(records);
    internal UsageSummary Summarize(UsagePeriod period) => _statistics.Summarize(period);
    internal UsageSummary SummarizeRange(DateOnly start, DateOnly end) => _statistics.SummarizeRange(start, end);
    internal static string? ValidateRange(DateOnly start, DateOnly end) => UsageStatistics.ValidateRange(start, end);
}
