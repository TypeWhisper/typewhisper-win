using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>Optional History-owned audio persistence, independent of recording recovery.</summary>
public interface IHistoryAudioService : IHistoryService
{
    /// <summary>Saves text and optionally an owned PCM copy; permission is checked before preparation and commit.</summary>
    HistoryAudioSaveResult TryAddRecordWithAudio(TranscriptionRecord record, float[] samples, int sampleRate,
        Func<bool> maySaveAudio, CancellationToken cancellationToken = default, Func<bool>? maySaveHistory = null);
    /// <summary>Visible pending audio cleanup or ownership error, if any.</summary>
    string? AudioCleanupError { get; }
    /// <summary>Retries only journaled work against the actual current History references.</summary>
    string? RetryAudioCleanup();
    /// <summary>Resolves an existing verified owned audio file; missing or unsafe files return null.</summary>
    string? ResolveAudioPath(string? fileName);
}
