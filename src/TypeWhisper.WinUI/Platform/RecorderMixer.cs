namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Provides the recorder mixdown helper.
/// </summary>
public static class RecorderMixer
{
    /// <summary>
    /// Mixes microphone and system audio for saved mixed recorder output.
    /// </summary>
    public static float[] MixForOutput(
        IReadOnlyList<float> micSamples,
        IReadOnlyList<float> systemSamples,
        RecorderMicDuckingMode duckingMode)
    {
        if (micSamples.Count == 0 && systemSamples.Count == 0)
            return [];
        if (micSamples.Count == 0)
            return systemSamples.Select(ClampSample).ToArray();
        if (systemSamples.Count == 0)
            return micSamples.Select(ClampSample).ToArray();

        var length = Math.Max(micSamples.Count, systemSamples.Count);
        var output = new float[length];
        var ducking = DuckingProfile.For(duckingMode);
        var micGain = 1f;
        var holdSamples = 0;

        for (var i = 0; i < length; i++)
        {
            var hasMic = i < micSamples.Count;
            var hasSystem = i < systemSamples.Count;
            var system = hasSystem ? systemSamples[i] : 0f;
            var mic = hasMic ? micSamples[i] : 0f;

            if (ducking.Enabled && hasSystem)
            {
                var level = Math.Abs(system);
                if (level >= ducking.HighThreshold)
                    holdSamples = ducking.HoldSamples;
                else if (level <= ducking.LowThreshold && holdSamples > 0)
                    holdSamples--;

                var targetGain = holdSamples > 0 ? ducking.MinimumGain : 1f;
                var coefficient = targetGain < micGain ? ducking.AttackCoefficient : ducking.ReleaseCoefficient;
                micGain += (targetGain - micGain) * coefficient;
            }

            var adjustedMic = mic * micGain;
            output[i] = (hasMic, hasSystem) switch
            {
                (true, true) => ClampSample(adjustedMic + system),
                (true, false) => ClampSample(adjustedMic),
                (false, true) => ClampSample(system),
                _ => 0f
            };
        }

        return output;
    }

    private static float ClampSample(float sample) =>
        Math.Clamp(sample, -1f, 1f);

    private readonly record struct DuckingProfile(
        bool Enabled,
        float MinimumGain,
        float LowThreshold,
        float HighThreshold,
        int HoldSamples,
        float AttackCoefficient,
        float ReleaseCoefficient)
    {
        public static DuckingProfile For(RecorderMicDuckingMode mode) =>
            mode switch
            {
                RecorderMicDuckingMode.Off => new(false, 1f, 0f, 0f, 0, 1f, 1f),
                RecorderMicDuckingMode.Medium => new(
                    true,
                    MinimumGain: 0.42f,
                    LowThreshold: 0.01f,
                    HighThreshold: 0.04f,
                    HoldSamples: (int)(0.08 * 16000),
                    AttackCoefficient: 0.035f,
                    ReleaseCoefficient: 0.2f),
                _ => new(
                    true,
                    MinimumGain: 0.18f,
                    LowThreshold: 0.006f,
                    HighThreshold: 0.025f,
                    HoldSamples: (int)(0.12 * 16000),
                    AttackCoefficient: 0.02f,
                    ReleaseCoefficient: 0.28f)
            };
    }
}
