namespace TypeWhisper.Windows.Services;

internal sealed record SimpleVoiceSegment(float[] Samples);

internal sealed class SimpleVoiceActivityDetector : IDisposable
{
    private readonly Queue<SimpleVoiceSegment> _segments = [];
    private readonly List<float> _currentSamples = [];
    private readonly float _speechThreshold;
    private readonly int _minSpeechSamples;
    private readonly int _minSilenceSamples;
    private readonly int _maxSegmentSamples;
    private int _speechSamples;
    private int _trailingSilenceSamples;
    private int _discardedShortSegmentCount;
    private bool _hasSpeech;
    private bool _disposed;

    public SimpleVoiceActivityDetector(
        int sampleRate = 16000,
        float speechThreshold = 0.02f,
        TimeSpan? minSpeechDuration = null,
        TimeSpan? minSilenceDuration = null,
        TimeSpan? maxSegmentDuration = null)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");

        _speechThreshold = speechThreshold;
        _minSpeechSamples = Math.Max(1, (int)Math.Round(sampleRate * (minSpeechDuration ?? TimeSpan.FromSeconds(0.25)).TotalSeconds));
        _minSilenceSamples = Math.Max(1, (int)Math.Round(sampleRate * (minSilenceDuration ?? TimeSpan.FromSeconds(0.5)).TotalSeconds));
        _maxSegmentSamples = Math.Max(1, (int)Math.Round(sampleRate * (maxSegmentDuration ?? TimeSpan.FromSeconds(60)).TotalSeconds));
    }

    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();

        foreach (var sample in samples)
        {
            if (Math.Abs(sample) >= _speechThreshold)
            {
                _hasSpeech = true;
                _trailingSilenceSamples = 0;
                _speechSamples++;
                _currentSamples.Add(sample);
                if (_currentSamples.Count >= _maxSegmentSamples)
                    CompleteCurrentSegment();

                continue;
            }

            if (!_hasSpeech)
                continue;

            _trailingSilenceSamples++;
            if (_trailingSilenceSamples >= _minSilenceSamples)
                CompleteCurrentSegment();
        }
    }

    public void Flush()
    {
        ThrowIfDisposed();
        CompleteCurrentSegment();
    }

    public bool IsEmpty()
    {
        ThrowIfDisposed();
        return _segments.Count == 0;
    }

    public SimpleVoiceSegment Front()
    {
        ThrowIfDisposed();
        return _segments.Peek();
    }

    public int DiscardedShortSegmentCount
    {
        get
        {
            ThrowIfDisposed();
            return _discardedShortSegmentCount;
        }
    }

    public void Pop()
    {
        ThrowIfDisposed();
        _segments.Dequeue();
    }

    public void Dispose()
    {
        _disposed = true;
        _segments.Clear();
        _currentSamples.Clear();
    }

    private void CompleteCurrentSegment()
    {
        if (_hasSpeech && _speechSamples >= _minSpeechSamples)
        {
            _segments.Enqueue(new SimpleVoiceSegment([.. _currentSamples]));
        }
        else if (_hasSpeech)
        {
            _discardedShortSegmentCount++;
        }

        _currentSamples.Clear();
        _speechSamples = 0;
        _trailingSilenceSamples = 0;
        _hasSpeech = false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
