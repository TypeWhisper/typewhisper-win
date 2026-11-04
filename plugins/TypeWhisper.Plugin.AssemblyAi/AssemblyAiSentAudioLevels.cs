namespace TypeWhisper.Plugin.AssemblyAi;

/// <summary>
/// The loudness of the PCM16 audio a live session sent, in 50 ms windows, to tell whether the audio after the last
/// transcribed word could hold another word. Ported from the macOS AssemblyAISentAudioLevels.
/// </summary>
internal sealed class AssemblyAiSentAudioLevels
{
    private const int WindowBytes = 1600; // 50 ms, mono PCM16 at 16 kHz.
    private const double WindowSeconds = 0.05;
    // A window is quiet below this share of the loudest window, and always below the absolute level.
    private const float RelativeQuietLevel = 0.15f;
    private const float AbsoluteQuietLevel = 0.003f;
    // Word end times can come a little early; the audio this soon after does not count.
    private const double WordEndTolerance = 0.1;

    private readonly List<float> _levels = [];
    private readonly List<byte> _partial = [];

    public void Append(ReadOnlySpan<byte> pcm16)
    {
        foreach (var value in pcm16) _partial.Add(value);
        var start = 0;
        while (_partial.Count - start >= WindowBytes)
        {
            _levels.Add(Level(_partial, start, WindowBytes));
            start += WindowBytes;
        }
        _partial.RemoveRange(0, start);
    }

    /// <summary>
    /// Whether every window that ends after <paramref name="seconds"/> (from the first sample) plus the tolerance is
    /// quiet. Audio after the end of the sent audio counts as quiet.
    /// </summary>
    public bool IsQuietAfter(double seconds)
    {
        var windows = new List<float>(_levels);
        if (_partial.Count >= 2) windows.Add(Level(_partial, 0, _partial.Count));
        var quiet = Math.Max(AbsoluteQuietLevel, (windows.Count == 0 ? 0 : windows.Max()) * RelativeQuietLevel);
        var first = Math.Max(0, (int)Math.Floor((seconds + WordEndTolerance) / WindowSeconds));
        for (var i = first; i < windows.Count; i++)
            if (windows[i] >= quiet) return false;
        return true;
    }

    private static float Level(List<byte> pcm16, int start, int count)
    {
        var samples = count / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm16[start + 2 * i] | pcm16[start + 2 * i + 1] << 8) / 32768.0;
            sum += sample * sample;
        }
        return (float)Math.Sqrt(sum / samples);
    }
}
