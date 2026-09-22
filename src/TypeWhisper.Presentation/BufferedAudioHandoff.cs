namespace TypeWhisper.Presentation;

/// <summary>Retains the beginning of a recording until its streaming consumer is ready.</summary>
public sealed class BufferedAudioHandoff(int maximumSamples = 16000 * 15)
{
    private readonly object _sync = new();
    private readonly Queue<float[]> _pending = new();
    private Action<float[]>? _consumer;
    private int _samples;
    private bool _enabled;
    private bool _overflowed;

    /// <summary>Begins a fresh recording; no audio is retained outside a recording.</summary>
    public void Begin()
    {
        lock (_sync) { ResetCore(); _enabled = true; }
    }

    /// <summary>Copies pending samples or forwards them synchronously in capture order.</summary>
    public void Append(float[] samples)
    {
        lock (_sync)
        {
            if (!_enabled || _overflowed || samples.Length == 0) return;
            if (_consumer is not null) { _consumer(samples); return; }
            if (samples.Length > maximumSamples - _samples)
            {
                _overflowed = true;
                _pending.Clear(); _samples = 0;
                return;
            }
            _pending.Enqueue((float[])samples.Clone());
            _samples += samples.Length;
        }
    }

    /// <summary>Atomically drains the prefix before forwarding new chunks. False requires full-recording fallback.</summary>
    public bool Attach(Action<float[]> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_sync)
        {
            if (!_enabled || _overflowed) return false;
            if (_consumer is not null) throw new InvalidOperationException("An audio consumer is already attached.");
            while (_pending.TryDequeue(out var chunk)) consumer(chunk);
            _samples = 0;
            _consumer = consumer;
            return true;
        }
    }

    /// <summary>Detaches the stream and discards its retained prefix.</summary>
    public void Reset() { lock (_sync) ResetCore(); }
    private void ResetCore()
    {
        _pending.Clear(); _samples = 0; _consumer = null; _enabled = false; _overflowed = false;
    }
}
