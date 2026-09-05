namespace TypeWhisper.WinUI;

internal enum DictationPhase { Idle, Recording, Processing, Error }
internal sealed record DictationOverlayState(DictationPhase Phase, TimeSpan Duration, string Message, string TargetApp, uint TargetProcessId = 0)
{
    internal string Label => Phase switch
    {
        DictationPhase.Recording => "RECORDING",
        DictationPhase.Processing => "TRANSCRIBING",
        DictationPhase.Error => "ERROR",
        _ => "READY"
    };
}
