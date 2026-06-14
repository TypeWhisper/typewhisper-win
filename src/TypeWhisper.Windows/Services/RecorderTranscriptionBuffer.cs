namespace TypeWhisper.Windows.Services;

/// <summary>
/// Stores microphone and system-audio samples separately and exposes recorder transcription buffers.
/// </summary>
public sealed class RecorderTranscriptionBuffer
{
    private const int SampleRate = 16000;
    private readonly RecorderMicDuckingMode _duckingMode;
    private readonly List<float> _micSamples = [];
    private readonly List<float> _systemSamples = [];
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the RecorderTranscriptionBuffer class.
    /// </summary>
    public RecorderTranscriptionBuffer(RecorderMicDuckingMode duckingMode)
    {
        _duckingMode = duckingMode;
    }

    /// <summary>
    /// Gets the maximum current sample length across active source buffers.
    /// </summary>
    public int SampleCount
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(_micSamples.Count, _systemSamples.Count);
            }
        }
    }

    /// <summary>
    /// Appends microphone samples.
    /// </summary>
    public void AppendMic(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0)
            return;

        lock (_lock)
        {
            _micSamples.AddRange(samples);
        }
    }

    /// <summary>
    /// Appends system-audio samples.
    /// </summary>
    public void AppendSystem(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0)
            return;

        lock (_lock)
        {
            _systemSamples.AddRange(samples);
        }
    }

    /// <summary>
    /// Returns the current mono transcription buffer.
    /// </summary>
    public float[] GetCurrentBuffer(bool micEnabled = true, bool systemEnabled = true)
    {
        lock (_lock)
        {
            return MixLocked(micEnabled, systemEnabled);
        }
    }

    /// <summary>
    /// Returns a recent mono transcription buffer.
    /// </summary>
    public float[] GetRecentBuffer(TimeSpan duration, bool micEnabled = true, bool systemEnabled = true)
    {
        var requestedSamples = Math.Max(0, (int)Math.Round(duration.TotalSeconds * SampleRate));
        if (requestedSamples == 0)
            return [];

        lock (_lock)
        {
            var mixed = MixLocked(micEnabled, systemEnabled);
            if (mixed.Length <= requestedSamples)
                return mixed;

            return mixed[^requestedSamples..];
        }
    }

    /// <summary>
    /// Returns mono transcription samples after the supplied mixed-buffer offset.
    /// </summary>
    public float[] GetBufferDelta(int previousSampleCount, bool micEnabled = true, bool systemEnabled = true)
    {
        lock (_lock)
        {
            var mixed = MixLocked(micEnabled, systemEnabled);
            if (previousSampleCount <= 0)
                return mixed;
            if (previousSampleCount >= mixed.Length)
                return [];

            return mixed[previousSampleCount..];
        }
    }

    /// <summary>
    /// Returns the final source samples.
    /// </summary>
    public (float[] MicSamples, float[] SystemSamples) GetSourceSamples()
    {
        lock (_lock)
        {
            return ([.. _micSamples], [.. _systemSamples]);
        }
    }

    private float[] MixLocked(bool micEnabled, bool systemEnabled)
    {
        IReadOnlyList<float> mic = micEnabled ? _micSamples : [];
        IReadOnlyList<float> system = systemEnabled ? _systemSamples : [];
        return RecorderMixer.Mix(mic, system, _duckingMode);
    }
}

/// <summary>
/// Provides recorder mixdown and track-layout helpers.
/// </summary>
public static class RecorderMixer
{
    /// <summary>
    /// Mixes microphone and system audio into mono transcription samples.
    /// </summary>
    public static float[] Mix(
        IReadOnlyList<float> micSamples,
        IReadOnlyList<float> systemSamples,
        RecorderMicDuckingMode duckingMode)
    {
        return MixCore(micSamples, systemSamples, duckingMode, averageOverlappingSources: true);
    }

    /// <summary>
    /// Mixes microphone and system audio for saved mixed recorder output.
    /// </summary>
    public static float[] MixForOutput(
        IReadOnlyList<float> micSamples,
        IReadOnlyList<float> systemSamples,
        RecorderMicDuckingMode duckingMode)
    {
        return MixCore(micSamples, systemSamples, duckingMode, averageOverlappingSources: false);
    }

    private static float[] MixCore(
        IReadOnlyList<float> micSamples,
        IReadOnlyList<float> systemSamples,
        RecorderMicDuckingMode duckingMode,
        bool averageOverlappingSources)
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
                (true, true) => ClampSample((adjustedMic + system) * (averageOverlappingSources ? 0.5f : 1f)),
                (true, false) => ClampSample(adjustedMic),
                (false, true) => ClampSample(system),
                _ => 0f
            };
        }

        return output;
    }

    /// <summary>
    /// Interleaves source samples as stereo mic-left/system-right frames.
    /// </summary>
    public static float[] InterleaveSeparateTracks(
        IReadOnlyList<float> micSamples,
        IReadOnlyList<float> systemSamples)
    {
        var length = Math.Max(micSamples.Count, systemSamples.Count);
        if (length == 0)
            return [];

        var output = new float[length * 2];
        for (var i = 0; i < length; i++)
        {
            output[i * 2] = i < micSamples.Count ? ClampSample(micSamples[i]) : 0f;
            output[i * 2 + 1] = i < systemSamples.Count ? ClampSample(systemSamples[i]) : 0f;
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
