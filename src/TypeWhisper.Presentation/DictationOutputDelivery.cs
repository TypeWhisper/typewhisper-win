using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Delivery outcome, retaining the original result for review when needed.</summary>
/// <param name="Record">The completed dictation.</param>
/// <param name="Saved">Whether the record was written to history.</param>
/// <param name="NeedsReview">Whether the host should present a transient review.</param>
/// <param name="Message">User-facing delivery status.</param>
public sealed record DictationOutputResult(TranscriptionRecord Record, bool Saved, bool NeedsReview, string Message)
{
    /// <summary>Whether a requested history write or paste failed; choosing review-first is not a failure.</summary>
    public bool Failed { get; init; }
}

/// <summary>Applies output choices without requiring Windows or a clipboard.</summary>
/// <param name="history">Explicit history destination.</param>
public sealed class DictationOutputDelivery(IHistoryService history)
{
    /// <summary>Saves when allowed, then attempts paste or returns a reviewable result.</summary>
    public async Task<DictationOutputResult> DeliverAsync(TranscriptionRecord record,
        DictationOutputPreferences atStart, Func<DictationOutputPreferences> current,
        Func<Task<bool>> paste, CancellationToken ct = default, float[]? samples = null, int sampleRate = 16000)
    {
        ct.ThrowIfCancellationRequested();
        var saved = false;
        if (atStart.RestrictedBy(current()).SaveToHistory)
        {
            try
            {
                await history.EnsureLoadedAsync();
                ct.ThrowIfCancellationRequested();
                if (atStart.RestrictedBy(current()).SaveToHistory)
                {
                    var wantsAudio = record.SourceKind == "dictation" && samples is { Length: > 0 }
                        && atStart.RestrictedBy(current()).SaveHistoryAudio;
                    string? audioWarning = null;
                    var suppressed = false;
                    if (wantsAudio && history is IHistoryAudioService audioHistory)
                    {
                        var audioRecord = record;
                        var result = await Task.Run(() => audioHistory.TryAddRecordWithAudio(audioRecord, samples!, sampleRate,
                            () => !ct.IsCancellationRequested && atStart.RestrictedBy(current()).SaveHistoryAudio, ct,
                            () => !ct.IsCancellationRequested && atStart.RestrictedBy(current()).SaveToHistory), ct);
                        record = result.Record;
                        saved = result.Saved;
                        audioWarning = result.Warning;
                        suppressed = result.Suppressed;
                        ct.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        saved = history.TryAddRecord(record);
                        if (wantsAudio) audioWarning = "History audio saving is unavailable. Only the text was retained.";
                    }
                    if (!saved && !suppressed)
                        return new(record, false, true, "History could not be saved. Review and copy your text; nothing was pasted.") { Failed = true };
                    if (!string.IsNullOrWhiteSpace(audioWarning))
                        return new(record, saved, true, (saved ? "Saved to History. " : "Not saved to History. ") + audioWarning + " Review and copy your text; nothing was pasted.") { Failed = true };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new(record, false, true, "History could not be saved. Review and copy your text; nothing was pasted.") { Failed = true };
            }
        }
        var storage = saved ? "Saved to History." : "Not saved to History.";
        ct.ThrowIfCancellationRequested();
        if (record.Status != TranscriptionRecordStatus.Succeeded || !atStart.RestrictedBy(current()).AutoPaste)
            return new(record, saved, true, storage + " Review and copy your text; nothing was pasted.");
        try
        {
            if (await paste()) return new(record, saved, false, "Paste sent. " + storage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new(record, saved, true, storage + " Paste was not completed. Review and copy your text.") { Failed = true };
    }
}
