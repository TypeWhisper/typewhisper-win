using TypeWhisper.Core.Services;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Contains stopped dictation samples and their pending recovery recording.
/// </summary>
public sealed record AudioRecordingResult(
    float[]? Samples,
    RecoveryRecordingLease? RecoveryLease);
