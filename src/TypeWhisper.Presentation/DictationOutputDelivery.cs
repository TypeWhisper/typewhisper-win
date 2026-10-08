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
        DictationReviewReason.PasteFailed => Loc.T("Text could not be inserted"),
        DictationReviewReason.ProcessingFailed => Loc.T("Text processing did not finish"),
        DictationReviewReason.ActionFailed => Loc.T("Workflow action needs attention"),
        _ => Loc.T("Your dictation")
    };
    /// <summary>Whether processing, storage or delivery failed; choosing review-first is not a failure. Storage failure alone does not require review.</summary>
    public bool Failed { get; init; }
    /// <summary>An action was attempted; its outcome must survive late cancellation.</summary>
    public bool ActionAttempted { get; init; }
    /// <summary>The paste was sent to the target field.</summary>
    public bool Inserted { get; init; }
    /// <summary>The host left the text on the clipboard after a failed paste.</summary>
    public bool CopiedToClipboard { get; init; }
    /// <summary>Text or an action already left TypeWhisper; a late cancel must not report it as discarded.</summary>
    public bool Committed => ActionAttempted || Inserted;
}

/// <summary>Applies output choices without requiring Windows or a clipboard.</summary>
/// <param name="history">Explicit history destination.</param>
public sealed class DictationOutputDelivery(IHistoryService history)
{
    /// <summary>What the History commit settled on; composed into the result only after the action or paste.</summary>
    private readonly record struct SaveOutcome(TranscriptionRecord Record, bool Saved, string? Warning);

    /// <summary>Saves when allowed and runs the action or paste while the commit is in flight; the result waits for both.</summary>
    public async Task<DictationOutputResult> DeliverAsync(TranscriptionRecord record,
        DictationOutputPreferences atStart, Func<DictationOutputPreferences> current,
        Func<Task<bool>> paste, CancellationToken ct = default, float[]? samples = null, int sampleRate = 16000,
        Func<CancellationToken, Task<WorkflowActionResult>>? action = null)
    {
        ct.ThrowIfCancellationRequested();
        var save = Task.FromResult(new SaveOutcome(record, false, null));
        if (atStart.RestrictedBy(current()).SaveToHistory)
        {
            try
            {
                await history.EnsureLoadedAsync();
                ct.ThrowIfCancellationRequested();
                // Rewriting a large history with fsync, plus encoding and hashing audio, takes 100-400 ms. The commit
                // starts here, but only the result waits for it: the action and paste below overlap it.
                if (atStart.RestrictedBy(current()).SaveToHistory) save = CommitAsync(record, atStart, current, ct, samples, sampleRate);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { save = Task.FromResult(new SaveOutcome(record, false, Loc.T("This dictation could not be saved to History."))); }
        }
        // The commit only adds an audio reference, so the original record decides the branch.
        var succeeded = record.Status == TranscriptionRecordStatus.Succeeded;
        WorkflowActionResult? actionResult = null;
        bool autoPaste = false, inserted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (succeeded && action is not null)
            {
                try { actionResult = await action(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { actionResult = new(false, Loc.T("Action completion is unknown. Check its destination before trying again.")); }
            }
            else if (succeeded)
            {
                autoPaste = atStart.RestrictedBy(current()).AutoPaste;
                if (autoPaste)
                {
                    try { inserted = await paste(); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Drain the commit so it never faults unobserved; its own recheck already honours ct.
            try { await save; } catch (OperationCanceledException) { }
            throw;
        }
        var committed = actionResult is not null || inserted;
        SaveOutcome outcome;
        try { outcome = await save; }
        // A worker cancelled before its commit saved nothing. Text or an action already left TypeWhisper, so the
        // result must say so instead of hiding it behind a cancellation.
        catch (OperationCanceledException) when (ct.IsCancellationRequested && committed) { outcome = new(record, false, null); }
        // With nothing delivered, a cancel during the commit still wins, as it did while the paste waited for it.
        if (!committed) ct.ThrowIfCancellationRequested();
        record = outcome.Record;
        var saved = outcome.Saved;
        var storageWarning = outcome.Warning;
        var storage = saved ? Loc.T("Saved to History.") : Loc.T("Not saved to History.");
        if (storageWarning is not null) storage += " " + storageWarning;
        if (actionResult is { } result)
            return new(record, saved, !result.Success, result.Success ? result.Message + " " + storage
                : result.Message + " " + Loc.T("Check the destination before trying again. You can copy the text below."))
                { Failed = !result.Success || storageWarning is not null, ActionAttempted = true,
                    ReviewReason = result.Success ? DictationReviewReason.None : DictationReviewReason.ActionFailed,
                    StorageWarning = storageWarning };
        if (!succeeded)
            return new(record, saved, true, Loc.T("Your speech was transcribed, but a processing step failed. Nothing was inserted. Check the text before copying it."))
                { Failed = true, ReviewReason = DictationReviewReason.ProcessingFailed, StorageWarning = storageWarning };
        if (!autoPaste)
            return new(record, saved, true, Loc.T("Automatic insertion is off. Copy the text, then paste it into the field you want to use."))
                { Failed = storageWarning is not null, ReviewReason = DictationReviewReason.AutomaticPasteDisabled, StorageWarning = storageWarning };
        if (inserted) return new(record, saved, false, Loc.T("Paste sent.") + " " + storage)
            { Failed = storageWarning is not null, Inserted = true, StorageWarning = storageWarning };
        return new(record, saved, true, Loc.T("TypeWhisper could not complete automatic insertion. Copy the text, check the intended field, and paste any missing text there."))
            { Failed = true, ReviewReason = DictationReviewReason.PasteFailed, StorageWarning = storageWarning };
    }

    /// <summary>Commits on a worker so the caller's UI thread stays free; the worker rechecks permission at the commit boundary, a failure becomes a warning and a cancel propagates.</summary>
    private async Task<SaveOutcome> CommitAsync(TranscriptionRecord record, DictationOutputPreferences atStart,
        Func<DictationOutputPreferences> current, CancellationToken ct, float[]? samples, int sampleRate)
    {
        try
        {
            var wantsAudio = record.SourceKind == "dictation" && samples is { Length: > 0 }
                && atStart.RestrictedBy(current()).SaveHistoryAudio;
            if (wantsAudio && history is IHistoryAudioService audioHistory)
            {
                var result = await Task.Run(() => audioHistory.TryAddRecordWithAudio(record, samples!, sampleRate,
                    () => !ct.IsCancellationRequested && atStart.RestrictedBy(current()).SaveHistoryAudio, ct,
                    () => !ct.IsCancellationRequested && atStart.RestrictedBy(current()).SaveToHistory), ct);
                return Outcome(result.Record, result.Saved, result.Suppressed, result.Warning);
            }
            var added = await Task.Run(() => !ct.IsCancellationRequested && atStart.RestrictedBy(current()).SaveToHistory
                ? history.TryAddRecord(record) : (bool?)null, ct);
            return Outcome(record, added == true, suppressed: added is null,
                wantsAudio && added is not null ? Loc.T("History audio saving is unavailable. Only the text was retained.") : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(record, false, Loc.T("This dictation could not be saved to History.")); }
    }

    /// <summary>An opt-out at the commit boundary is not a failure; a failed commit warns, and an audio warning is appended.</summary>
    private static SaveOutcome Outcome(TranscriptionRecord record, bool saved, bool suppressed, string? audioWarning)
    {
        var warning = saved || suppressed ? null : Loc.T("This dictation could not be saved to History.");
        if (!string.IsNullOrWhiteSpace(audioWarning)) warning = string.IsNullOrEmpty(warning) ? audioWarning : warning + " " + audioWarning;
        return new(record, saved, warning);
    }
}
