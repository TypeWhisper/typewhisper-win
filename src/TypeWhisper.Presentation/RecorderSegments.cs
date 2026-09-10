namespace TypeWhisper.Presentation;

/// <summary>Retains ordered active audio segments; pauses contribute no samples to the output timeline.</summary>
public sealed class RecorderSegments
{
    /// <summary>The fixed output sample rate.</summary>
    public const int SampleRate = 16000;
    /// <summary>The maximum active samples in one recording.</summary>
    public const int MaximumSamples = SampleRate * 60 * 60;
    private readonly List<ArraySegment<float>> _segments = [];
    /// <summary>The number of retained samples.</summary>
    public int SampleCount { get; private set; }
    /// <summary>The actual retained active duration.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRate);
    // Reserve bookkeeping before stopping sources, so retaining a stopped buffer cannot allocate.
    internal void PrepareAppend() => _segments.EnsureCapacity(_segments.Count + 1);
    /// <summary>Appends a stopped segment up to the remaining active-time limit.</summary>
    public void Add(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var count = Math.Min(samples.Length, MaximumSamples - SampleCount);
        if (count == 0) return;
        _segments.Add(new ArraySegment<float>(samples, 0, count));
        SampleCount += count;
    }
    /// <summary>Combines retained segments without adding gaps. Call off the UI thread after capture has stopped.</summary>
    public float[] Combine()
    {
        if (_segments.Count == 1 && _segments[0].Count == _segments[0].Array!.Length) return _segments[0].Array!;
        var result = new float[SampleCount];
        var offset = 0;
        foreach (var segment in _segments) { segment.AsSpan().CopyTo(result.AsSpan(offset)); offset += segment.Count; }
        return result;
    }
    /// <summary>Releases segment references after publication or transfer into a retry buffer.</summary>
    public void Clear() { _segments.Clear(); SampleCount = 0; }
    /// <summary>Clips or pads a source to the shared active segment end, preserving silence and source alignment.</summary>
    public static float[] FitTimeline(float[] samples, TimeSpan duration)
    {
        var count = (int)Math.Clamp(Math.Round(duration.TotalSeconds * SampleRate), 0, MaximumSamples);
        if (samples.Length == count) return samples;
        var result = new float[count];
        Array.Copy(samples, result, Math.Min(samples.Length, count));
        return result;
    }
}
