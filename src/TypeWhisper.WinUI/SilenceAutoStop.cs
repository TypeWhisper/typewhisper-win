namespace TypeWhisper.WinUI;

// Energy-based silence detection, not linguistic voice activity detection.
// The caller supplies monotonic elapsed time; audio callbacks and UI ticks may race.
internal sealed class SilenceAutoStop(TimeSpan timeout)
{
    internal const float ActivityThreshold = 0.008f;
    private readonly object _sync = new();
    private TimeSpan _lastActivity;
    private bool _heard;

    // Whether any level reached the activity threshold.
    internal bool HasActivity { get { lock (_sync) return _heard; } }

    internal void Observe(TimeSpan elapsed, float rms)
    {
        if (!float.IsFinite(rms) || rms < ActivityThreshold) return;
        lock (_sync)
        {
            _heard = true;
            if (elapsed > _lastActivity) _lastActivity = elapsed;
        }
    }

    internal bool ShouldStop(TimeSpan elapsed, bool modifiersHeld)
    {
        lock (_sync)
            return !modifiersHeld && elapsed - _lastActivity >= timeout;
    }
}
