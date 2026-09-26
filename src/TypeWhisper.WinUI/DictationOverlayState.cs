using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal enum DictationPhase { Idle, Recording, Processing, Error, Configuring, Completed, LoadingModel }
internal sealed record DictationOverlayState(DictationPhase Phase, TimeSpan Duration, string Message, string TargetApp, uint TargetProcessId = 0,
    RecordingMode RecordingMode = RecordingMode.Hybrid, string? CancelWarning = null, bool Cancelled = false)
{
    // A pending Escape warning replaces the phase label until it is confirmed or expires.
    internal bool ShowsCancelWarning => CancelWarning is not null && Phase is DictationPhase.Recording or DictationPhase.Processing;
    // After an Escape cancellation the idle overlay briefly confirms it, as on macOS.
    internal bool ShowsCancelled => Cancelled && Phase == DictationPhase.Idle;
    internal string AccessibleMessage => ShowsCancelWarning ? CancelWarning! : ShowsCancelled ? "Cancelled" : Message;

    internal static DictationPhase VisiblePhase(DictationPhase phase, bool dictationAttempted) =>
        phase == DictationPhase.LoadingModel && !dictationAttempted ? DictationPhase.Configuring : phase;

    internal bool ShouldShowTranscript(bool enabled, bool supportsLiveTranscription) => enabled &&
        (Phase == DictationPhase.Completed || supportsLiveTranscription &&
            Phase is DictationPhase.Recording or DictationPhase.Processing or DictationPhase.Error);

    internal string RecordingModeLabel => RecordingMode switch
    {
        RecordingMode.Toggle => "Toggle",
        RecordingMode.Hold => "Hold",
        _ => "Hybrid"
    };
    internal string Label => ShowsCancelWarning ? "PRESS ESC AGAIN" : ShowsCancelled ? "CANCELLED" : Phase switch
    {
        DictationPhase.LoadingModel => "LOADING MODEL",
        DictationPhase.Recording => "RECORDING",
        DictationPhase.Processing => "TRANSCRIBING",
        DictationPhase.Error => "ERROR",
        DictationPhase.Completed => "DONE",
        _ => "READY"
    };
}
