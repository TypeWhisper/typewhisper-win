using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

// Read-only projection. IDs and unknown metadata are never written back to disk.
internal static class HistoryEntryAdapter
{
    internal static PrototypeHistoryEntry FromRecord(TranscriptionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Id) || record.RawText is null || record.FinalText is null)
            throw new InvalidDataException("A history record is missing required fields.");
        var id = Guid.TryParse(record.Id, out var parsed) && parsed != Guid.Empty
            ? parsed : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("history:" + record.Id)).AsSpan(0, 16));
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc));
        var text = record.DisplayText;
        var title = string.IsNullOrWhiteSpace(text) ? "Untitled transcript" : text.Replace('\r', ' ').Replace('\n', ' ');
        if (title.Length > 80) title = title[..80] + "…";
        return new PrototypeHistoryEntry(id,
            new PrototypeHistoryContent(timestamp, timestamp,
                new PrototypeHistoryOrigin("unknown", "Unknown", "Unknown device"),
                "legacy", PrototypeHistoryEntryKind.Unknown, title,
                double.IsFinite(record.DurationSeconds) ? Math.Max(0, record.DurationSeconds) : 0,
                record.Status == TranscriptionRecordStatus.Succeeded ? PrototypeHistoryProcessingState.Ready : PrototypeHistoryProcessingState.Failed,
                new PrototypeHistoryTranscript(record.RawText, text), record.Language,
                EngineName: record.EngineUsed, ModelName: record.ModelUsed,
                FailureMessage: record.WorkflowFailureMessage),
            new PrototypeHistoryInbox(timestamp));
    }
}
