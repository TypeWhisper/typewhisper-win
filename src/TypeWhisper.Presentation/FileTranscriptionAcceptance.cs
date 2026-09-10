using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Presentation;

/// <summary>Commits side effects only after the queue has accepted a file result.</summary>
public static class FileTranscriptionAcceptance
{
    /// <summary>Synchronously saves permitted history and snippet usage. Failures add warnings without discarding accepted text.</summary>
    /// <param name="result">An accepted result with preauthorized immutable history data.</param>
    /// <param name="history">The history service already loaded before acceptance.</param>
    /// <param name="current">The output preferences at the instant of acceptance.</param>
    /// <param name="recordUsage">Records actual expanded IDs once; returns a recoverable warning when unsuccessful.</param>
    public static string? Commit(FileTranscriptionOutput result, IHistoryService history,
        DictationOutputPreferences current, Func<IReadOnlyList<string>, string?> recordUsage)
    {
        var warnings = new List<string>();
        if (current.SaveToHistory && result.PendingHistory is { } record)
        {
            try
            {
                if (!history.TryAddRecord(record))
                    warnings.Add("History could not be saved. Your transcript remains available here for export.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { warnings.Add("History could not be saved. Your transcript remains available here for export."); }
        }
        try { if (recordUsage(result.AppliedSnippetIds) is { } warning) warnings.Add(warning); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { warnings.Add("Snippet usage could not be saved. Your transcript is unchanged."); }
        return warnings.Count == 0 ? null : string.Join(" · ", warnings);
    }
}
