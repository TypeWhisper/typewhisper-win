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

    string ExportToText(IReadOnlyList<TranscriptionRecord> records);
    string ExportToCsv(IReadOnlyList<TranscriptionRecord> records);
}
