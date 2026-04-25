using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

public sealed class AudioRecordingService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private const float AgcTargetRms = 0.1f;
    private const float AgcMaxGain = 20f;
    private const float AgcMinGain = 1f;
    private const float NormalizationTarget = 0.707f;
    private static readonly TimeSpan StopDrainDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Minimum per-chunk RMS level to consider as containing speech.
    /// Below this threshold, audio is treated as silence.
    /// </summary>
    public const float SpeechEnergyThreshold = 0.01f;

    private WaveInEvent? _waveIn;
    private WaveInEvent? _previewWaveIn;
    private List<float>? _sampleBuffer;
    private readonly object _bufferLock = new();
    private bool _isRecording;
    private bool _isWarmedUp;
    private bool _isPreviewing;
    private bool _disposed;
    private DateTime _recordingStartTime;
    private int? _configuredDeviceNumber;
    private int _activeDeviceNumber;
    private float _peakRmsLevel;
    private float _preGainPeakRms;
    private float _currentRmsLevel;
    private System.Timers.Timer? _devicePollTimer;
    private int _lastKnownDeviceCount;

    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    public event EventHandler<AudioLevelEventArgs>? PreviewLevelChanged;
    public event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;
    public event EventHandler? DevicesChanged;
    public event EventHandler? DeviceLost;
    public event EventHandler? DeviceAvailable;

    public bool HasDevice => WaveInEvent.DeviceCount > 0;
    public bool WhisperModeEnabled { get; set; }
    public bool NormalizationEnabled { get; set; } = true;
    public bool IsRecording => _isRecording;
    public float PeakRmsLevel => _peakRmsLevel;
    public float PreGainPeakRmsLevel => _preGainPeakRms;
    public float CurrentRmsLevel => _currentRmsLevel;
    public bool HasSpeechEnergy => _preGainPeakRms >= SpeechEnergyThreshold;
    public TimeSpan RecordingDuration => _isRecording ? DateTime.UtcNow - _recordingStartTime : TimeSpan.Zero;

    public void SetMicrophoneDevice(int? deviceNumber)
    {
        var newDevice = deviceNumber ?? FindBestMicrophoneDevice();
        if (_isWarmedUp && newDevice != _activeDeviceNumber)
        {
            DisposeWaveIn();
            _isWarmedUp = false;
        }
        _configuredDeviceNumber = deviceNumber;
        _activeDeviceNumber = newDevice;
    }

    public bool WarmUp()
    {
        if (_isWarmedUp || _disposed) return _isWarmedUp;

        if (WaveInEvent.DeviceCount == 0)
        {
            System.Diagnostics.Debug.WriteLine("WarmUp: No audio input devices available.");
            StartDevicePolling();
            return false;
        }

        _activeDeviceNumber = _configuredDeviceNumber ?? FindBestMicrophoneDevice();
        if (_activeDeviceNumber < 0)
        {
            StartDevicePolling();
            return false;
        }

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _activeDeviceNumber,
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 30
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();

            _isWarmedUp = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WarmUp failed: {ex.Message}");
            DisposeWaveIn();
        }

        StartDevicePolling();
        return _isWarmedUp;
    }

    public static IReadOnlyList<(int DeviceNumber, string Name)> GetAvailableDevices()
    {
        var devices = new List<(int, string)>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    public void StartRecording()
    {
        if (_isRecording) return;

        // The settings microphone preview uses its own WaveIn instance and can
        // block real capture while the settings window stays open on Dictation.
        // Always stop preview before entering recording mode.
        if (_isPreviewing)
            StopPreview();

        if (!_isWarmedUp && !WarmUp())
            return;

        if (_waveIn is null) return;

        _sampleBuffer = new List<float>(SampleRate * 60); // Pre-alloc ~1 min
        _peakRmsLevel = 0;
        _preGainPeakRms = 0;
        _recordingStartTime = DateTime.UtcNow;
        _isRecording = true;
    }

    public float[]? GetCurrentBuffer()
    {
        if (!_isRecording || _sampleBuffer is null) return null;
        lock (_bufferLock) { return [.. _sampleBuffer]; }
    }

    public float[]? StopRecording()
    {
        if (!_isRecording || _waveIn is null)
            return null;

        _isRecording = false;

        float[]? samples;
        lock (_bufferLock)
        {
            samples = _sampleBuffer?.ToArray();
            _sampleBuffer = null;
        }

        if (samples is null || samples.Length == 0)
            return null;

        if (NormalizationEnabled)
            NormalizeAudio(samples);

        return samples;
    }

    public async Task<float[]?> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRecording || _waveIn is null)
            return null;

        try
        {
            await Task.Delay(StopDrainDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Still stop and return the samples captured so far.
        }

        return StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording) return;

        var sampleCount = e.BytesRecorded / 2;
        float agcGain = 1f;

        // Compute pre-gain RMS for speech energy detection (unaffected by AGC)
        float preGainSum = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            preGainSum += s * s;
        }
        var preGainRms = MathF.Sqrt(preGainSum / sampleCount);
        if (preGainRms > _preGainPeakRms) _preGainPeakRms = preGainRms;

        if (WhisperModeEnabled)
        {
            if (preGainRms > 0.0001f)
                agcGain = Math.Clamp(AgcTargetRms / preGainRms, AgcMinGain, AgcMaxGain);
        }

        float peak = 0;
        float sumSquares = 0;
        var chunkBuffer = new float[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

            if (WhisperModeEnabled)
                sample = Math.Clamp(sample * agcGain, -1f, 1f);

            chunkBuffer[i] = sample;

            var abs = MathF.Abs(sample);
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        lock (_bufferLock)
        {
            _sampleBuffer?.AddRange(chunkBuffer);
        }

        var rms = MathF.Sqrt(sumSquares / sampleCount);
        _currentRmsLevel = rms;
        if (rms > _peakRmsLevel) _peakRmsLevel = rms;

        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms));

        if (SamplesAvailable is not null && _sampleBuffer is not null)
        {
            SamplesAvailable.Invoke(this, new SamplesAvailableEventArgs(chunkBuffer));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) { }

    private static void NormalizeAudio(float[] samples)
    {
        float peakAmplitude = 0;
        foreach (var s in samples)
        {
            var abs = MathF.Abs(s);
            if (abs > peakAmplitude) peakAmplitude = abs;
        }

        if (peakAmplitude < 0.01f) return;

        var gain = NormalizationTarget / peakAmplitude;
        if (gain <= 1.0f) return;

        for (var i = 0; i < samples.Length; i++)
            samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
    }

    private static int FindBestMicrophoneDevice()
    {
        var deviceCount = WaveInEvent.DeviceCount;

        for (var i = 0; i < deviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (caps.ProductName.Contains("Microphone", StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < deviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (!caps.ProductName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                !caps.ProductName.Contains("Mix", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return deviceCount > 0 ? 0 : -1;
    }

    private void StartDevicePolling()
    {
        _lastKnownDeviceCount = WaveInEvent.DeviceCount;
        _devicePollTimer?.Dispose();
        _devicePollTimer = new System.Timers.Timer(2000);
        _devicePollTimer.Elapsed += (_, _) => CheckForDeviceChanges();
        _devicePollTimer.AutoReset = true;
        _devicePollTimer.Start();
    }

    private void CheckForDeviceChanges()
    {
        try
        {
            var currentCount = WaveInEvent.DeviceCount;
            if (currentCount == _lastKnownDeviceCount) return;

            var previousCount = _lastKnownDeviceCount;
            _lastKnownDeviceCount = currentCount;
            DevicesChanged?.Invoke(this, EventArgs.Empty);

            if (currentCount == 0 && _isWarmedUp)
            {
                DeviceLost?.Invoke(this, EventArgs.Empty);
                DisposeWaveIn();
                _configuredDeviceNumber = null;
            }
            else if (currentCount > 0 && previousCount == 0)
            {
                DeviceAvailable?.Invoke(this, EventArgs.Empty);
                WarmUp();
            }
            else if (_isWarmedUp && _activeDeviceNumber >= currentCount)
            {
                DeviceLost?.Invoke(this, EventArgs.Empty);
                DisposeWaveIn();
                _configuredDeviceNumber = null;
                WarmUp();
            }
        }
        catch { }
    }

    public void StartPreview(int? deviceNumber)
    {
        if (_isRecording)
            return;

        StopPreview();
        if (_disposed || WaveInEvent.DeviceCount == 0) return;

        var deviceIndex = deviceNumber ?? FindBestMicrophoneDevice();
        if (deviceIndex < 0) return;

        try
        {
            _previewWaveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 50
            };
            _previewWaveIn.DataAvailable += OnPreviewDataAvailable;
            _previewWaveIn.StartRecording();
            _isPreviewing = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartPreview failed: {ex.Message}");
            StopPreview();
        }
    }

    public void StopPreview()
    {
        if (_previewWaveIn is not null)
        {
            _previewWaveIn.DataAvailable -= OnPreviewDataAvailable;
            try { _previewWaveIn.StopRecording(); } catch { }
            _previewWaveIn.Dispose();
            _previewWaveIn = null;
        }
        _isPreviewing = false;
    }

    public bool IsPreviewing => _isPreviewing;

    private void OnPreviewDataAvailable(object? sender, WaveInEventArgs e)
    {
        var sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        float peak = 0;
        float sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            var abs = MathF.Abs(sample);
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        var rms = MathF.Sqrt(sumSquares / sampleCount);
        PreviewLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms));
    }

    private void DisposeWaveIn()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.Dispose();
            _waveIn = null;
        }
        _isWarmedUp = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _devicePollTimer?.Dispose();
            _isRecording = false;
            StopPreview();
            DisposeWaveIn();
            _disposed = true;
        }
    }
}

public sealed record AudioLevelEventArgs(float PeakLevel, float RmsLevel);

public sealed class SamplesAvailableEventArgs(float[] samples) : EventArgs
{
    public float[] Samples { get; } = samples;
}
