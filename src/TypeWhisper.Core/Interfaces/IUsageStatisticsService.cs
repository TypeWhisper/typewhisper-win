using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Stores retention-independent aggregates for successful dictations.
/// </summary>
public interface IUsageStatisticsService
{
    /// <summary>
    /// Gets the persisted daily statistics in chronological order.
    /// </summary>
    IReadOnlyList<UsageStatisticsDaySnapshot> Days { get; }

    /// <summary>
    /// Gets whether any statistics have been recorded.
    /// </summary>
    bool HasAnyStatistics { get; }

    /// <summary>
    /// Raised after the persisted aggregates change.
    /// </summary>
    event Action? StatisticsChanged;

    /// <summary>
    /// Records one successful dictation.
    /// </summary>
    void RecordTranscription(
        DateTime timestamp,
        int wordCount,
        double durationSeconds,
        string? appIdentifier,
        string? appName,
        string? engineUsed,
        string? modelUsed);

    /// <summary>
    /// Imports existing history once without coupling later statistics to history retention.
    /// </summary>
    void BackfillFromHistoryIfNeeded(IEnumerable<TranscriptionRecord> records);

    /// <summary>
    /// Replaces all aggregates with the supplied records.
    /// </summary>
    void ReplaceWithHistoryRecords(IEnumerable<TranscriptionRecord> records);
}
