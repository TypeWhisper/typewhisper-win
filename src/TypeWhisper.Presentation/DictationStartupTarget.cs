namespace TypeWhisper.Presentation;

/// <summary>Retains the original capture target while allowing the app's own tray controls.</summary>
public static class DictationStartupTarget
{
    /// <summary>Rejects unrelated focus changes and replaced or exited target processes.</summary>
    public static bool IsValid(nint target, nint foreground, nint tray, uint expectedProcess, uint currentProcess) =>
        target != 0 && expectedProcess != 0 && currentProcess == expectedProcess &&
        (foreground == target || (tray != 0 && foreground == tray));
}
