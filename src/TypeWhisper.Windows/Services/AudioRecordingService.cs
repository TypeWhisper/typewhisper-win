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

    private WaveInEvent? _waveIn;
    private List<float>? _sampleBuffer;
    private bool _isRecording;
    private bool _isWarmedUp;
    private bool _disposed;
    private DateTime _recordingStartTime;
    private int? _configuredDeviceNumber;
    private int _activeDeviceNumber;
    private float _peakRmsLevel;
    private float _currentRmsLevel;
    private System.Timers.Timer? _devicePollTimer;
    private int _lastKnownDeviceCount;

    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    public event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;
    public event EventHandler? DevicesChanged;
    public event EventHandler? DeviceLost;

    public bool WhisperModeEnabled { get; set; }
    public bool NormalizationEnabled { get; set; } = true;
    public bool IsRecording => _isRecording;
    public float PeakRmsLevel => _peakRmsLevel;
    public float CurrentRmsLevel => _currentRmsLevel;
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

    public void WarmUp()
    {
        if (_isWarmedUp || _disposed) return;

        _activeDeviceNumber = _configuredDeviceNumber ?? FindBestMicrophoneDevice();

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
        StartDevicePolling();
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

        if (!_isWarmedUp)
            WarmUp();

        if (_waveIn is null) return;

        _sampleBuffer = new List<float>(SampleRate * 60); // Pre-alloc ~1 min
        _peakRmsLevel = 0;
        _recordingStartTime = DateTime.UtcNow;
        _isRecording = true;
    }

    public float[]? StopRecording()
    {
        if (!_isRecording || _waveIn is null)
            return null;

        _isRecording = false;

        var samples = _sampleBuffer?.ToArray();
        _sampleBuffer = null;

        if (samples is null || samples.Length == 0)
            return null;

        if (NormalizationEnabled)
            NormalizeAudio(samples);

        return samples;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording) return;

        var sampleCount = e.BytesRecorded / 2;
        float agcGain = 1f;

        if (WhisperModeEnabled)
        {
            float preGainSum = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var s = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                preGainSum += s * s;
            }
            var preGainRms = MathF.Sqrt(preGainSum / sampleCount);
            if (preGainRms > 0.0001f)
                agcGain = Math.Clamp(AgcTargetRms / preGainRms, AgcMinGain, AgcMaxGain);
        }

        float peak = 0;
        float sumSquares = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

            if (WhisperModeEnabled)
                sample = Math.Clamp(sample * agcGain, -1f, 1f);

            _sampleBuffer?.Add(sample);

            var abs = MathF.Abs(sample);
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        var rms = MathF.Sqrt(sumSquares / sampleCount);
        _currentRmsLevel = rms;
        if (rms > _peakRmsLevel) _peakRmsLevel = rms;

        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms));

        if (SamplesAvailable is not null && _sampleBuffer is not null)
        {
            var chunkSamples = new float[sampleCount];
            _sampleBuffer.CopyTo(_sampleBuffer.Count - sampleCount, chunkSamples, 0, sampleCount);
            SamplesAvailable.Invoke(this, new SamplesAvailableEventArgs(chunkSamples));
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

        return 0;
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
            if (currentCount != _lastKnownDeviceCount)
            {
                _lastKnownDeviceCount = currentCount;
                DevicesChanged?.Invoke(this, EventArgs.Empty);

                if (_isWarmedUp && _activeDeviceNumber >= currentCount)
                {
                    DeviceLost?.Invoke(this, EventArgs.Empty);
                    DisposeWaveIn();
                    _configuredDeviceNumber = null;
                    WarmUp();
                }
            }
        }
        catch { }
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
