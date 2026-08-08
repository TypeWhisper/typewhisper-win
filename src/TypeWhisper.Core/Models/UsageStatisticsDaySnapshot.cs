namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents the retention-independent usage aggregates for one local calendar day.
/// </summary>
public sealed record UsageStatisticsDaySnapshot
{
    /// <summary>
    /// Gets the local calendar day.
    /// </summary>
    public required DateTime Day { get; init; }

    /// <summary>
    /// Gets the number of successful dictations.
    /// </summary>
    public int TranscriptionCount { get; init; }

    /// <summary>
    /// Gets the total number of dictated words.
    /// </summary>
    public int TotalWords { get; init; }

    /// <summary>
    /// Gets the total recorded audio duration.
    /// </summary>
    public double TotalDurationSeconds { get; init; }

    /// <summary>
    /// Gets per-application dictation counts.
    /// </summary>
    public IReadOnlyDictionary<string, int> AppCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets per-engine and model dictation counts.
    /// </summary>
    public IReadOnlyDictionary<string, int> ModelCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets per-hour dictation counts, indexed from 0 through 23.
    /// </summary>
    public IReadOnlyList<int> HourCounts { get; init; } = new int[24];
}
