using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides recent transcription store behavior.
/// </summary>
public sealed class RecentTranscriptionStore
{
    private readonly object _gate = new();
    private readonly int _maxSessionEntries;
    private readonly List<RecentTranscriptionEntry> _sessionEntries = [];

    /// <summary>
    /// Initializes a new instance of the RecentTranscriptionStore class.
    /// </summary>
    public RecentTranscriptionStore(int maxSessionEntries = 20)
    {
        _maxSessionEntries = Math.Max(1, maxSessionEntries);
    }

    /// <summary>
    /// Gets the session entries.
    /// </summary>
    /// <summary>
    /// Gets the session entries.
    /// </summary>
    public IReadOnlyList<RecentTranscriptionEntry> SessionEntries
    {
        get
        {
            lock (_gate)
            {
                return _sessionEntries.ToList();
            }
        }
    }

    /// <summary>
    /// Records transcription.
    /// </summary>
    public void RecordTranscription(
        string id,
        string finalText,
        DateTime timestamp,
        string? appName,
        string? appProcessName)
    {
        var trimmedText = finalText.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(trimmedText))
            return;

        lock (_gate)
        {
            _sessionEntries.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            _sessionEntries.Insert(0, new RecentTranscriptionEntry(
                id,
                trimmedText,
                timestamp,
                appName,
                appProcessName,
                RecentTranscriptionSource.Session));

            if (_sessionEntries.Count > _maxSessionEntries)
                _sessionEntries.RemoveRange(_maxSessionEntries, _sessionEntries.Count - _maxSessionEntries);
        }
    }

    /// <summary>
    /// Returns the merged entries.
    /// </summary>
    public IReadOnlyList<RecentTranscriptionEntry> MergedEntries(
        IReadOnlyList<TranscriptionRecord> historyRecords,
        int limit = 12)
    {
        if (limit <= 0)
            return [];

        List<RecentTranscriptionEntry> sessionSnapshot;
        lock (_gate)
        {
            sessionSnapshot = _sessionEntries.ToList();
        }

        var historyEntries = historyRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.FinalText))
            .Select(record => new RecentTranscriptionEntry(
                record.Id,
                record.FinalText.Trim(),
                record.Timestamp,
                record.AppName,
                record.AppProcessName,
                RecentTranscriptionSource.History));

        var merged = sessionSnapshot
            .Concat(historyEntries)
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.Source == RecentTranscriptionSource.Session ? 0 : 1)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return merged
            .Where(entry => seen.Add(entry.Id))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Returns the latest entry.
    /// </summary>
    public RecentTranscriptionEntry? LatestEntry(IReadOnlyList<TranscriptionRecord> historyRecords) =>
        MergedEntries(historyRecords, limit: 1).FirstOrDefault();
}

/// <summary>
/// Represents recent transcription entry data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="FinalText">Final text supplied to the member.</param>
/// <param name="Timestamp">Timestamp supplied to the member.</param>
/// <param name="AppName">App name supplied to the member.</param>
/// <param name="AppProcessName">App process name supplied to the member.</param>
/// <param name="Source">Source supplied to the member.</param>
public sealed record RecentTranscriptionEntry(
    string Id,
    string FinalText,
    DateTime Timestamp,
    string? AppName,
    string? AppProcessName,
    RecentTranscriptionSource Source);

/// <summary>
/// Lists the supported recent transcription source values.
/// </summary>
public enum RecentTranscriptionSource
{
    /// <summary>
    /// Represents the session option.
    /// </summary>
    Session,
    /// <summary>
    /// Represents the history option.
    /// </summary>
    History
}
