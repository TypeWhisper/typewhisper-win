using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

// Read-only projection. IDs and unknown metadata are never written back to disk.
internal static class HistoryEntryAdapter
{
    internal static HistoryEntry FromRecord(TranscriptionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Id) || record.RawText is null || record.FinalText is null)
            throw new InvalidDataException("A history record is missing required fields.");
        var id = Guid.TryParse(record.Id, out var parsed) && parsed != Guid.Empty
            ? parsed : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("history:" + record.Id)).AsSpan(0, 16));
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc));
        var text = record.DisplayText;
        var title = string.IsNullOrWhiteSpace(text) ? "Untitled transcript" : text.Replace('\r', ' ').Replace('\n', ' ');
        if (title.Length > 80) title = title[..80] + "…";
        var kind = record.SourceKind switch
        {
            "dictation" => HistoryEntryKind.Dictation,
            "recording" => HistoryEntryKind.Recording,
            "file" => HistoryEntryKind.ImportedFile,
            _ => HistoryEntryKind.Unknown
        };
        return new HistoryEntry(id,
            new HistoryContent(timestamp, timestamp,
                new HistoryOrigin("unknown", "Unknown", "Unknown device"),
                string.IsNullOrWhiteSpace(record.SourceKind) ? "legacy" : record.SourceKind, kind, title,
                double.IsFinite(record.DurationSeconds) ? Math.Max(0, record.DurationSeconds) : 0,
                record.Status == TranscriptionRecordStatus.Succeeded ? HistoryProcessingState.Ready : HistoryProcessingState.Failed,
                new HistoryTranscript(record.RawText, text), record.Language,
                EngineName: record.EngineUsed, ModelName: record.ModelUsed,
                FailureMessage: record.Status == TranscriptionRecordStatus.TextProcessorFailed
                    ? "Text processing failed. The preceding transcript was retained." : record.WorkflowFailureMessage, AppName: record.AppName,
                AppProcessName: record.AppProcessName, TranscriptionTaskUsed: record.TranscriptionTaskUsed,
                WorkflowName: record.ProfileName, WorkflowId: record.WorkflowId, TextProcessors: record.TextProcessors?.ToArray()),
            new HistoryInbox(timestamp)) { PersistedRecordId = record.Id };
    }
}
