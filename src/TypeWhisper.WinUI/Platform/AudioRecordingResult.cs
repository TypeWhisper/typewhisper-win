using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Contains stopped dictation samples and their pending recovery recording.
/// </summary>
public sealed record AudioRecordingResult(
    float[]? Samples,
    RecoveryRecordingLease? RecoveryLease);
