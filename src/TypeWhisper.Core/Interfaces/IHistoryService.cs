using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the history service contract.
/// </summary>
public interface IHistoryService
{
    /// <summary>
    /// Gets the persisted transcription history records.
    /// </summary>
    IReadOnlyList<TranscriptionRecord> Records { get; }
    /// <summary>
    /// Raised when records changes.
    /// </summary>
    event Action? RecordsChanged;

    /// <summary>
    /// Adds record.
    /// </summary>
    void AddRecord(TranscriptionRecord record);
    /// <summary>
    /// Updates record.
    /// </summary>
    void UpdateRecord(string id, string finalText);
    /// <summary>
    /// Deletes record.
    /// </summary>
    void DeleteRecord(string id);
    /// <summary>
    /// Clears all items from the current collection.
    /// </summary>
    void ClearAll();
    /// <summary>
    /// Performs search.
    /// </summary>
    IReadOnlyList<TranscriptionRecord> Search(string query);
    /// <summary>
    /// Performs purge old records.
    /// </summary>
    void PurgeOldRecords(TimeSpan? retention);

    /// <summary>
    /// Gets the number of persisted transcription history records.
    /// </summary>
    int TotalRecords { get; }
    /// <summary>
    /// Gets the total words.
    /// </summary>
    int TotalWords { get; }
    /// <summary>
    /// Gets the total duration.
    /// </summary>
    double TotalDuration { get; }

    /// <summary>
    /// Ensures loaded asynchronously..
    /// </summary>
    Task EnsureLoadedAsync();
    /// <summary>
    /// Returns distinct apps.
    /// </summary>
    IReadOnlyList<string> GetDistinctApps();

    /// <summary>
    /// Exports to text.
    /// </summary>
    string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    /// <summary>
    /// Exports to csv.
    /// </summary>
    string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    /// <summary>
    /// Exports to markdown.
    /// </summary>
    string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    /// <summary>
    /// Exports the current data as JSON.
    /// </summary>
    string ExportToJson(IReadOnlyList<TranscriptionRecord> records);
}
