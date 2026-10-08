namespace TypeWhisper.WinUI.Platform;

public sealed partial class AudioRecordingService
{
    private readonly float[] _preroll = new float[SampleRate / 2];
    private int _prerollCount, _prerollNext;
    private long _prerollLastSample;
    private bool _prerollEnabled;
    private volatile bool _prerollArmed;
    private volatile bool _prerollSuspended;

    /// <summary>Opts into a half-second memory-only microphone buffer between dictations. Off by default.</summary>
    public bool MicrophonePrerollEnabled
    {
        get => _prerollEnabled;
        set
        {
            lock (_captureLifecycleLock)
            {
                if (_prerollEnabled == value || _disposed) return;
                _prerollEnabled = value;
                if (_isRecording) return; // Apply the new policy after this recording ends.
                DisposeWaveIn(reason: "pre-roll preference changed");
                WarmUp();
            }
        }
    }

    /// <summary>Releases an idle pre-roll microphone during sleep, screen lock, or session disconnection.</summary>
    public void SuspendMicrophonePreroll(bool suspended)
    {
        lock (_captureLifecycleLock)
        {
            if (_disposed || _prerollSuspended == suspended) return;
            _prerollSuspended = suspended;
            ClearPreroll();
            if (!_prerollEnabled || _isRecording) return;
            if (suspended) DisposeWaveIn(reason: "pre-roll suspended");
            else WarmUp();
        }
    }

    private bool CanArmPreroll => _prerollEnabled && !_prerollSuspended
        && !ReleaseCaptureBetweenRecordings() && !_disposed && !_isPreviewing;

    private void TryArmPreroll()
    {
        if (!CanArmPreroll || _isRecording || _prerollArmed || _waveIn is null) return;
        ClearPreroll();
        _prerollArmed = true;
        try { _waveIn.StartRecording(); }
        catch
        {
            _prerollArmed = false;
            throw;
        }
    }

    // Called under the sample-buffer lock. Idle audio never raises events or enters recovery storage.
    private void BufferPreroll(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _preroll[_prerollNext] = sample;
            _prerollNext = (_prerollNext + 1) % _preroll.Length;
            _prerollCount = Math.Min(_prerollCount + 1, _preroll.Length);
        }
        _prerollLastSample = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private void ClearPreroll()
    {
        lock (_bufferLock)
        {
            Array.Clear(_preroll);
            _prerollCount = _prerollNext = 0;
            _prerollLastSample = 0;
        }
    }

    private void AppendPrerollToRecording()
    {
        lock (_bufferLock)
        {
            if (!_prerollArmed || _prerollCount == 0 || _prerollLastSample == 0
                || System.Diagnostics.Stopwatch.GetElapsedTime(_prerollLastSample) > TimeSpan.FromMilliseconds(250))
            {
                ClearPreroll();
                return;
            }
            var prefix = new float[_prerollCount];
            var start = (_prerollNext - _prerollCount + _preroll.Length) % _preroll.Length;
            double energy = 0;
            for (var i = 0; i < prefix.Length; i++)
            {
                prefix[i] = _preroll[(start + i) % _preroll.Length];
                energy += prefix[i] * prefix[i];
            }
            _preGainPeakRms = (float)Math.Sqrt(energy / prefix.Length);
            if (WhisperModeEnabled && _preGainPeakRms > 0.0001f)
            {
                var gain = Math.Clamp(AgcTargetRms / _preGainPeakRms, AgcMinGain, AgcMaxGain);
                for (var i = 0; i < prefix.Length; i++) prefix[i] = Math.Clamp(prefix[i] * gain, -1f, 1f);
            }
            _sampleBuffer!.InsertRange(0, prefix);
            _recordingStartTime -= TimeSpan.FromSeconds(prefix.Length / (double)SampleRate);
            if (_activeRecoveryRecordingId is { } id) _recoveryStore?.AppendSamples(id, prefix);
            RaiseSamplesAvailable(prefix);
            ClearPreroll();
        }
    }
}
