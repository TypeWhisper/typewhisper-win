using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal enum DictationPhase { Idle, Recording, Processing, Error, Configuring, Completed }
internal sealed record DictationOverlayState(DictationPhase Phase, TimeSpan Duration, string Message, string TargetApp, uint TargetProcessId = 0,
    RecordingMode RecordingMode = RecordingMode.Hybrid)
{
    internal bool ShouldShowTranscript(bool enabled, bool supportsLiveTranscription) => enabled &&
        (Phase == DictationPhase.Completed || supportsLiveTranscription &&
            Phase is DictationPhase.Recording or DictationPhase.Processing or DictationPhase.Error);

    internal string RecordingModeLabel => RecordingMode switch
    {
        RecordingMode.Toggle => "Toggle",
        RecordingMode.Hold => "Hold",
        _ => "Hybrid"
    };
    internal string Label => Phase switch
    {
        DictationPhase.Recording => "RECORDING",
        DictationPhase.Processing => "TRANSCRIBING",
        DictationPhase.Error => "ERROR",
        DictationPhase.Completed => "DONE",
        _ => "READY"
    };
}
