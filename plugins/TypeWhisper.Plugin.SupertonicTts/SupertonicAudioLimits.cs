namespace TypeWhisper.Plugin.SupertonicTts;

internal static class SupertonicAudioLimits
{
    private const int MaxDurationSeconds = 120;
    private const long MaxPcmBytes = 12L * 1024 * 1024;

    internal static long MaximumSamples(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new InvalidOperationException("The generated speech has an invalid sample rate.");
        return Math.Min((long)sampleRate * MaxDurationSeconds, MaxPcmBytes / sizeof(short));
    }

    internal static void ValidateSampleCount(double sampleCount, long maximumSamples)
    {
        if (!double.IsFinite(sampleCount) || sampleCount < 0 || sampleCount > maximumSamples)
            throw new InvalidOperationException("The generated speech exceeds the two-minute audio limit.");
    }
}
