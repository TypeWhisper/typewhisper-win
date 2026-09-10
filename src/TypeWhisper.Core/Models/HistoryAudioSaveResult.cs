namespace TypeWhisper.Core.Models;

/// <summary>The actual record committed to History and any nonfatal audio warning.</summary>
/// <param name="Record">Actual text/audio reference, including text-only fallback.</param>
/// <param name="Saved">Whether History committed this record.</param>
/// <param name="Warning">Optional audio preparation or cleanup warning.</param>
public sealed record HistoryAudioSaveResult(TranscriptionRecord Record, bool Saved, string? Warning)
{
    /// <summary>History was deliberately not written because permission was withdrawn; not a storage failure.</summary>
    public bool Suppressed { get; init; }
}

/// <summary>Original mono PCM input, without decoder padding.</summary>
/// <param name="Samples">Source samples supplied explicitly by the capture owner.</param>
/// <param name="SampleRate">Actual sample rate.</param>
public sealed record HistoryAudioInput(float[] Samples, int SampleRate = 16000);
