namespace TypeWhisper.Presentation;

/// <summary>Applies the previous Windows probability threshold to final transcription results.</summary>
public static class FinalSpeechPolicy
{
    /// <summary>Rejects likely silence unless genuine preview text or the captured quiet-clip preference confirms intent.</summary>
    /// <remarks>Missing probabilities remain unknown. Empty final text without a preview is always rejected.</remarks>
    public static bool ShouldReject(string? finalText, float? noSpeechProbability,
        bool hasConfirmedPreviewText, bool recognizeQuietClips)
    {
        if (string.IsNullOrWhiteSpace(finalText)) return !hasConfirmedPreviewText;
        return noSpeechProbability is > 0.8f && !recognizeQuietClips && !hasConfirmedPreviewText;
    }
}
