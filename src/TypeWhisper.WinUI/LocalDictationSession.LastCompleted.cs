using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal TypeWhisper.Core.Models.TranscriptionRecord? LastApiDictationRecord { get; private set; }
    private readonly LastCompletedDictationStore _lastCompletedDictation = new();
    internal LastCompletedDictation? LastCompletedDictation => _lastCompletedDictation.Current;
}
