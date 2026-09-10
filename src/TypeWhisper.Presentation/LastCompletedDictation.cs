using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>A detached final dictation held only for this application's current lifetime.</summary>
/// <param name="Id">The completed recording identifier.</param>
/// <param name="Text">Exact final text, without truncation or a raw-text fallback.</param>
/// <param name="Language">Reported or explicitly chosen language, when known.</param>
/// <param name="Engine">Engine captured for the recording, when known.</param>
/// <param name="Model">Model captured for the recording, when known.</param>
/// <param name="RecordedAt">Recording timestamp supplied by the completed result.</param>
/// <param name="CompletedAt">UTC time at publication.</param>
public sealed record LastCompletedDictation(string Id, string Text, string? Language, string? Engine,
    string? Model, DateTime RecordedAt, DateTime CompletedAt);

/// <summary>
/// Retains one successful final dictation in RAM. Delivery failure (History or paste) does not invalidate its text.
/// No history read, file write, clipboard operation or automatic speech is performed.
/// </summary>
public sealed class LastCompletedDictationStore
{
    /// <summary>Maximum UTF-8 byte count of the final text; larger results are rejected without truncation.</summary>
    public const int MaximumTextBytes = 1024 * 1024;
    /// <summary>Maximum UTF-8 byte count of each identifier or language/engine/model metadata value.</summary>
    public const int MaximumMetadataBytes = 4096;
    private readonly object _sync = new();
    private LastCompletedDictation? _current;
    private bool _closed;

    /// <summary>Current detached result, or null before the first successful dictation or after shutdown.</summary>
    public LastCompletedDictation? Current { get { lock (_sync) return _current; } }

    /// <summary>
    /// Publishes only nonempty successful dictation output. Review-first and History-off are eligible, as are
    /// completed texts whose History write or paste failed. Invalid, oversized, canceled or closed requests return
    /// false and do not replace a previous result. The caller must pass only the final delivery outcome.
    /// </summary>
    public bool TryPublish(DictationOutputResult outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var record = outcome.Record;
        if (cancellationToken.IsCancellationRequested || record.SourceKind != "dictation"
            || record.Status != TranscriptionRecordStatus.Succeeded
            || string.IsNullOrWhiteSpace(record.FinalText) || string.IsNullOrWhiteSpace(record.Id)
            || !Fits(record.FinalText, MaximumTextBytes) || !Fits(record.Id, MaximumMetadataBytes)
            || !Fits(record.Language, MaximumMetadataBytes) || !Fits(record.EngineUsed, MaximumMetadataBytes)
            || !Fits(record.ModelUsed, MaximumMetadataBytes)) return false;
        var snapshot = new LastCompletedDictation(record.Id, record.FinalText, record.Language, record.EngineUsed,
            record.ModelUsed, record.Timestamp, DateTime.UtcNow);
        lock (_sync)
        {
            // Close and cancellation are checked again after construction, at the publication boundary.
            if (_closed || cancellationToken.IsCancellationRequested) return false;
            _current = snapshot;
            return true;
        }
    }

    /// <summary>Permanently stops publication and releases the stored text reference; safe to call repeatedly.</summary>
    public void Close()
    {
        lock (_sync) { _closed = true; _current = null; }
    }

    private static bool Fits(string? value, int limit) => value is null ||
        (value.Length <= limit && Encoding.UTF8.GetByteCount(value) <= limit);
}
