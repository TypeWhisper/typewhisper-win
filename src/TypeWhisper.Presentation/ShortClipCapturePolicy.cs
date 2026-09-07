namespace TypeWhisper.Presentation;

/// <summary>The capture decision before final transcription.</summary>
public enum ShortClipCaptureDecision
{
    /// <summary>The captured audio may be decoded.</summary>
    Transcribe,
    /// <summary>Less than forty milliseconds of audio was captured.</summary>
    TooShort,
    /// <summary>The unamplified signal did not meet the speech energy threshold.</summary>
    NoSpeech
}

/// <summary>Ports the Windows dictation capture thresholds and trailing silence policy for 16 kHz audio.</summary>
public static class ShortClipCapturePolicy
{
    /// <summary>Classifies original audio, using genuine recognized preview text only as optional evidence.</summary>
    public static ShortClipCaptureDecision Classify(double rawDuration, float preGainPeakRms,
        bool hasConfirmedText, bool recognizeQuietClips = false)
    {
        if (!double.IsFinite(rawDuration) || rawDuration < 0.04) return ShortClipCaptureDecision.TooShort;
        if (hasConfirmedText) return ShortClipCaptureDecision.Transcribe;
        var threshold = rawDuration < 1 ? 0.003f : 0.006f;
        return recognizeQuietClips || (float.IsFinite(preGainPeakRms) && preGainPeakRms >= threshold)
            ? ShortClipCaptureDecision.Transcribe : ShortClipCaptureDecision.NoSpeech;
    }

    /// <summary>Appends silence without shifting original samples; callers retain original audio for history and CTC.</summary>
    public static float[] PadForFinalDecode(float[] originalSamples)
    {
        ArgumentNullException.ThrowIfNull(originalSamples);
        var length = originalSamples.Length < 12000 ? 12000 : checked(originalSamples.Length + 4800);
        var padded = new float[length];
        originalSamples.CopyTo(padded, 0);
        return padded;
    }
}
