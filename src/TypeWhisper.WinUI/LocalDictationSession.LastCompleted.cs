using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly LastCompletedDictationStore _lastCompletedDictation = new();
    internal LastCompletedDictation? LastCompletedDictation => _lastCompletedDictation.Current;
}
