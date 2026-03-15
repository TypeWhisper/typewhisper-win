using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IHistoryService
{
    IReadOnlyList<TranscriptionRecord> Records { get; }
    event Action? RecordsChanged;

    void AddRecord(TranscriptionRecord record);
    void UpdateRecord(string id, string finalText);
    void DeleteRecord(string id);
    void ClearAll();
    IReadOnlyList<TranscriptionRecord> Search(string query);
    void PurgeOldRecords(int retentionDays);

    int TotalRecords { get; }
    int TotalWords { get; }
    double TotalDuration { get; }

    Task EnsureLoadedAsync();
    IReadOnlyList<string> GetDistinctApps();

    string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    string ExportToJson(IReadOnlyList<TranscriptionRecord> records);
}
