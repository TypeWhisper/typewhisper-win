using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>
/// Provides a read-only history boundary for desktop presentation layers.
/// Does not infer device identity or recording kind from legacy audio metadata.
/// </summary>
public sealed class HistoryReader
{
    private readonly IHistoryService _history;

    /// <summary>Creates a reader over the application's existing history service.</summary>
    public HistoryReader(IHistoryService history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    /// <summary>
    /// Loads a snapshot ordered newest first. Search matches raw and final text;
    /// an optional app filter matches the stored app name exactly, ignoring case.
    /// The caller owns UI dispatch and refresh timing.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionRecord>> ReadAsync(
        string? query = null, string? appName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _history.EnsureLoadedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var term = query?.Trim() ?? "";
        var app = string.IsNullOrWhiteSpace(appName) ? null : appName.Trim();
        var records = _history.Records;
        var result = records.Where(record =>
                (app is null || string.Equals(record.AppName, app, StringComparison.OrdinalIgnoreCase)) &&
                (term.Length == 0 || record.RawText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 record.FinalText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(record => record.Timestamp)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(result);
    }
}
