using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Confirmed outcome of a workflow destination, without automatic retries.</summary>
public sealed record WorkflowActionResult(bool Success, string Message);

/// <summary>Why a dictation result needs the user's attention.</summary>
public enum DictationReviewReason
{
    /// <summary>No manual text recovery is needed.</summary>
    None,
    /// <summary>The effective output preferences disable automatic insertion.</summary>
    AutomaticPasteDisabled,
    /// <summary>Automatic insertion could not be completed.</summary>
    PasteFailed,
    /// <summary>A workflow or text processor failed before delivery.</summary>
    ProcessingFailed,
    /// <summary>A workflow destination did not confirm success.</summary>
    ActionFailed
}

/// <summary>Delivery outcome, retaining the original result for review when needed.</summary>
/// <param name="Record">The completed dictation.</param>
/// <param name="Saved">Whether the record was written to history.</param>
/// <param name="NeedsReview">Whether the host should present a transient review.</param>
/// <param name="Message">User-facing delivery status.</param>
public sealed record DictationOutputResult(TranscriptionRecord Record, bool Saved, bool NeedsReview, string Message)
{
    /// <summary>Explicit reason, independent of history storage and translated UI text.</summary>
    public DictationReviewReason ReviewReason { get; init; }
    /// <summary>A storage problem that must not prevent successful text delivery.</summary>
    public string? StorageWarning { get; init; }
    /// <summary>Describes the result rather than requiring an unexplained review step.</summary>
    public string ReviewTitle => ReviewReason switch
    {
        DictationReviewReason.PasteFailed => "Text could not be inserted",
        DictationReviewReason.ProcessingFailed => "Text processing did not finish",
        DictationReviewReason.ActionFailed => "Workflow action needs attention",
        _ => "Your dictation"
    };
    /// <summary>Whether processing, storage or delivery failed; choosing review-first is not a failure. Storage failure alone does not require review.</summary>
    public bool Failed { get; init; }
    /// <summary>An action was attempted; its outcome must survive late cancellation.</summary>
    public bool ActionAttempted { get; init; }
    /// <summary>The paste was sent to the target field.</summary>
    public bool Inserted { get; init; }
    /// <summary>Text or an action already left TypeWhisper; a late cancel must not report it as discarded.</summary>
    public bool Committed => ActionAttempted || Inserted;
}

/// <summary>Applies output choices without requiring Windows or a clipboard.</summary>
/// <param name="history">Explicit history destination.</param>
public sealed class DictationOutputDelivery(IHistoryService history)
{
    /// <summary>Saves when allowed, then attempts paste or returns a reviewable result.</summary>
    public async Task<DictationOutputResult> DeliverAsync(TranscriptionRecord record,
        DictationOutputPreferences atStart, Func<DictationOutputPreferences> current,
        Func<Task<bool>> paste, CancellationToken ct = default, float[]? samples = null, int sampleRate = 16000,
        Func<CancellationToken, Task<WorkflowActionResult>>? action = null)
    {
        ct.ThrowIfCancellationRequested();
        var saved = false;
        string? storageWarning = null;
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
                        // Rewriting a large history must not stall the caller's UI thread before paste.
                        var textRecord = record;
                        saved = await Task.Run(() => history.TryAddRecord(textRecord), ct);
                        if (wantsAudio) audioWarning = "History audio saving is unavailable. Only the text was retained.";
                    }
                    if (!saved && !suppressed)
                        storageWarning = "This dictation could not be saved to History.";
                    if (!string.IsNullOrWhiteSpace(audioWarning))
                        storageWarning = string.IsNullOrEmpty(storageWarning) ? audioWarning : storageWarning + " " + audioWarning;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                storageWarning = "This dictation could not be saved to History.";
            }
        }
        var storage = saved ? "Saved to History." : "Not saved to History.";
        if (storageWarning is not null) storage += " " + storageWarning;
        ct.ThrowIfCancellationRequested();
        if (record.Status == TranscriptionRecordStatus.Succeeded && action is not null)
        {
            WorkflowActionResult result;
            try { result = await action(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { result = new(false, "Action completion is unknown. Check its destination before trying again."); }
            return new(record, saved, !result.Success, result.Success ? result.Message + " " + storage
                : result.Message + " Check the destination before trying again. You can copy the text below.")
                { Failed = !result.Success || storageWarning is not null, ActionAttempted = true,
                    ReviewReason = result.Success ? DictationReviewReason.None : DictationReviewReason.ActionFailed,
                    StorageWarning = storageWarning };
        }
        if (record.Status != TranscriptionRecordStatus.Succeeded)
            return new(record, saved, true, "Your speech was transcribed, but a processing step failed. Nothing was inserted. Check the text before copying it.")
                { Failed = true, ReviewReason = DictationReviewReason.ProcessingFailed, StorageWarning = storageWarning };
        if (!atStart.RestrictedBy(current()).AutoPaste)
            return new(record, saved, true, "Automatic insertion is off. Copy the text, then paste it into the field you want to use.")
                { Failed = storageWarning is not null, ReviewReason = DictationReviewReason.AutomaticPasteDisabled, StorageWarning = storageWarning };
        try
        {
            if (await paste()) return new(record, saved, false, "Paste sent. " + storage)
                { Failed = storageWarning is not null, Inserted = true, StorageWarning = storageWarning };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new(record, saved, true, "TypeWhisper could not complete automatic insertion. Copy the text, check the intended field, and paste any missing text there.")
            { Failed = true, ReviewReason = DictationReviewReason.PasteFailed, StorageWarning = storageWarning };
    }
}
