using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal long ApiDictationGeneration { get; private set; }
    internal long LastApiDictationRecordGeneration { get; private set; }
    internal void BeginApiDictationGeneration() => ApiDictationGeneration++;
    internal void PublishApiDictationRecord(TranscriptionRecord record)
    {
        LastApiDictationRecord = record;
        LastApiDictationRecordGeneration = ApiDictationGeneration;
    }
}
