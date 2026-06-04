using NAudio;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides audio recording service behavior.
/// </summary>
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
    private static readonly TimeSpan DefaultDevicePollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Minimum per-chunk RMS level to consider as containing speech.
    /// Below this threshold, audio is treated as silence.
    /// </summary>
    public const float SpeechEnergyThreshold = 0.01f;

    private readonly IAudioInputDeviceProvider _deviceProvider;
    private readonly IAudioInputCaptureFactory _captureFactory;
    private readonly TimeSpan _devicePollInterval;
    private IAudioInputCapture? _waveIn;
    private IAudioInputCapture? _previewWaveIn;
    private List<float>? _sampleBuffer;
    private readonly object _bufferLock = new();
    private bool _isRecording;
    private bool _isWarmedUp;
    private bool _isPreviewing;
    private bool _disposed;
    private DateTime _recordingStartTime;
    private int? _configuredDeviceNumber;
    private string? _configuredDeviceName;
    private int _activeDeviceNumber = -1;
    private string? _activeDeviceName;
    private float _peakRmsLevel;
    private float _preGainPeakRms;
    private float _currentRmsLevel;
    private System.Timers.Timer? _devicePollTimer;
    private string _lastKnownDeviceSignature = "";
    private bool _lastKnownHasDevices;
    private bool _lastKnownPreferredDeviceAvailable;
    private bool _lastKnownSnapshotInitialized;
    private const int TailDiagnosticChunkLimit = 8;
    private readonly Queue<AudioChunkTelemetry> _recentChunks = new();
    private DateTime? _lastSamplesAvailableUtc;

    /// <summary>
    /// Initializes a new instance of the AudioRecordingService class.
    /// </summary>
    public AudioRecordingService()
        : this(new WaveInAudioInputDeviceProvider(), new WaveInAudioInputCaptureFactory(), DefaultDevicePollInterval)
    {
    }

    internal AudioRecordingService(
        IAudioInputDeviceProvider deviceProvider,
        IAudioInputCaptureFactory captureFactory,
        TimeSpan devicePollInterval)
    {
        _deviceProvider = deviceProvider;
        _captureFactory = captureFactory;
        _devicePollInterval = devicePollInterval;
    }

    /// <summary>
    /// Raised when audio level changes.
    /// </summary>
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    /// <summary>
    /// Raised when preview level changes.
    /// </summary>
    public event EventHandler<AudioLevelEventArgs>? PreviewLevelChanged;
    /// <summary>
    /// Raised when samples available.
    /// </summary>
    public event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;
    /// <summary>
    /// Raised when devices changes.
    /// </summary>
    public event EventHandler? DevicesChanged;
    /// <summary>
    /// Raised when device lost.
    /// </summary>
    public event EventHandler? DeviceLost;
    /// <summary>
    /// Raised when device available.
    /// </summary>
    public event EventHandler? DeviceAvailable;

    /// <summary>
    /// Gets whether has device.
    /// </summary>
    public bool HasDevice => _deviceProvider.DeviceCount > 0;
    /// <summary>
    /// Gets or sets the whisper mode enabled value.
    /// </summary>
    public bool WhisperModeEnabled { get; set; }
    /// <summary>
    /// Gets or sets the normalization enabled value.
    /// </summary>
    public bool NormalizationEnabled { get; set; } = true;
    /// <summary>
    /// Gets whether recording is currently active.
    /// </summary>
    public bool IsRecording => _isRecording;
    /// <summary>
    /// Gets the peak rms level.
    /// </summary>
    public float PeakRmsLevel => _peakRmsLevel;
    /// <summary>
    /// Gets the pre gain peak rms level.
    /// </summary>
    public float PreGainPeakRmsLevel => _preGainPeakRms;
    /// <summary>
    /// Gets the current rms level.
    /// </summary>
    public float CurrentRmsLevel => _currentRmsLevel;
    /// <summary>
    /// Gets whether has speech energy.
    /// </summary>
    public bool HasSpeechEnergy => _preGainPeakRms >= SpeechEnergyThreshold;
    /// <summary>
    /// Gets the recording duration.
    /// </summary>
    public TimeSpan RecordingDuration => _isRecording ? DateTime.UtcNow - _recordingStartTime : TimeSpan.Zero;

    /// <summary>
    /// Sets microphone device.
    /// </summary>
    public void SetMicrophoneDevice(int? deviceNumber)
    {
        var previousDeviceNumber = _configuredDeviceNumber;
        _configuredDeviceNumber = deviceNumber;

        if (deviceNumber is int explicitDeviceNumber)
        {
            var deviceName = TryGetDeviceName(explicitDeviceNumber);
            if (deviceName is not null
                && (previousDeviceNumber != explicitDeviceNumber || string.IsNullOrWhiteSpace(_configuredDeviceName)))
            {
                _configuredDeviceName = deviceName;
            }
        }
        else
        {
            _configuredDeviceName = null;
        }

        var newDevice = ResolvePreferredDeviceNumber(allowFallback: true);
        if (_isWarmedUp && newDevice != _activeDeviceNumber)
        {
            DisposeWaveIn();
            if (newDevice >= 0)
                WarmUp();
        }
        else if (newDevice >= 0)
        {
            _activeDeviceNumber = newDevice;
        }
    }

    /// <summary>
    /// Performs warm up.
    /// </summary>
    public bool WarmUp()
    {
        if (_isWarmedUp || _disposed) return _isWarmedUp;

        if (_deviceProvider.DeviceCount == 0)
        {
            System.Diagnostics.Debug.WriteLine("WarmUp: No audio input devices available.");
            StartDevicePolling();
            return false;
        }

        _activeDeviceNumber = ResolvePreferredDeviceNumber(allowFallback: true);
        if (_activeDeviceNumber < 0)
        {
            StartDevicePolling();
            return false;
        }

        try
        {
            _waveIn = _captureFactory.Create(
                _activeDeviceNumber,
                new WaveFormat(SampleRate, BitsPerSample, Channels),
                bufferMilliseconds: 30);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();

            _activeDeviceName = TryGetDeviceName(_activeDeviceNumber);
            _isWarmedUp = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WarmUp failed: {ex.Message}");
            DisposeWaveIn(stopRecording: false);
        }

        StartDevicePolling();
        return _isWarmedUp;
    }

    /// <summary>
    /// Returns available devices.
    /// </summary>
    public static IReadOnlyList<(int DeviceNumber, string Name)> GetAvailableDevices() =>
        GetAvailableDevices(new WaveInAudioInputDeviceProvider());

    /// <summary>
    /// Returns available input devices.
    /// </summary>
    public IReadOnlyList<(int DeviceNumber, string Name)> GetAvailableInputDevices() =>
        GetAvailableDevices(_deviceProvider);

    /// <summary>
    /// Starts recording.
    /// </summary>
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
        _currentRmsLevel = 0;
        lock (_bufferLock)
        {
            _recentChunks.Clear();
            _lastSamplesAvailableUtc = null;
        }
        _recordingStartTime = DateTime.UtcNow;
        _isRecording = true;
    }

    /// <summary>
    /// Returns current buffer.
    /// </summary>
    public float[]? GetCurrentBuffer()
    {
        if (!_isRecording || _sampleBuffer is null) return null;
        lock (_bufferLock) { return [.. _sampleBuffer]; }
    }

    /// <summary>
    /// Stops recording.
    /// </summary>
    public float[]? StopRecording()
    {
        if (!_isRecording)
            return null;

        if (_waveIn is null)
        {
            ClearRecordingState();
            return null;
        }

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

    /// <summary>
    /// Stops recording asynchronously.
    /// </summary>
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

    internal AudioTailSnapshot CaptureTailSnapshot()
    {
        lock (_bufferLock)
        {
            return new AudioTailSnapshot(
                _lastSamplesAvailableUtc,
                [.. _recentChunks]);
        }
    }

    private void OnDataAvailable(object? sender, AudioInputDataAvailableEventArgs e)
    {
        if (!_isRecording) return;

        var sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

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

        var rms = MathF.Sqrt(sumSquares / sampleCount);

        lock (_bufferLock)
        {
            _sampleBuffer?.AddRange(chunkBuffer);
            _lastSamplesAvailableUtc = DateTime.UtcNow;
            _recentChunks.Enqueue(new AudioChunkTelemetry(
                _lastSamplesAvailableUtc.Value,
                peak,
                rms,
                preGainRms,
                sampleCount));
            while (_recentChunks.Count > TailDiagnosticChunkLimit)
                _recentChunks.Dequeue();
        }

        _currentRmsLevel = rms;
        if (rms > _peakRmsLevel) _peakRmsLevel = rms;

        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms));

        if (SamplesAvailable is not null && _sampleBuffer is not null)
        {
            SamplesAvailable.Invoke(this, new SamplesAvailableEventArgs(chunkBuffer));
        }
    }

    private void OnRecordingStopped(object? sender, AudioInputRecordingStoppedEventArgs e)
    {
        if (_waveIn is null || !ReferenceEquals(sender, _waveIn))
            return;

        System.Diagnostics.Debug.WriteLine(e.Exception is null
            ? "Audio input capture stopped unexpectedly."
            : $"Audio input capture stopped unexpectedly: {e.Exception.Message}");

        ClearRecordingState();
        DisposeWaveIn(stopRecording: false);
        StartDevicePolling();
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }

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

    private int FindBestMicrophoneDevice()
    {
        var deviceCount = _deviceProvider.DeviceCount;

        for (var i = 0; i < deviceCount; i++)
        {
            var name = TryGetDeviceName(i);
            if (name is not null
                && (name.Contains("Microphone", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        for (var i = 0; i < deviceCount; i++)
        {
            var name = TryGetDeviceName(i);
            if (name is not null
                && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Mix", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return deviceCount > 0 ? 0 : -1;
    }

    private int ResolvePreferredDeviceNumber(bool allowFallback)
    {
        if (_configuredDeviceNumber is int configuredDeviceNumber)
        {
            var configuredDeviceName = TryGetDeviceName(configuredDeviceNumber);
            if (configuredDeviceName is not null
                && (string.IsNullOrWhiteSpace(_configuredDeviceName)
                    || string.Equals(configuredDeviceName, _configuredDeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                _configuredDeviceName = configuredDeviceName;
                return configuredDeviceNumber;
            }

            var rememberedDeviceNumber = FindDeviceByName(_configuredDeviceName);
            if (rememberedDeviceNumber >= 0)
                return rememberedDeviceNumber;

            return allowFallback ? FindBestMicrophoneDevice() : -1;
        }

        return FindBestMicrophoneDevice();
    }

    private int FindDeviceByName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return -1;

        for (var i = 0; i < _deviceProvider.DeviceCount; i++)
        {
            var currentName = TryGetDeviceName(i);
            if (string.Equals(currentName, deviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private string? TryGetDeviceName(int deviceNumber)
    {
        if (deviceNumber < 0 || deviceNumber >= _deviceProvider.DeviceCount)
            return null;

        try
        {
            return _deviceProvider.GetDeviceName(deviceNumber);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or MmException)
        {
            return null;
        }
    }

    private void StartDevicePolling()
    {
        UpdateKnownDeviceSnapshot();
        _devicePollTimer?.Dispose();
        _devicePollTimer = null;

        if (_devicePollInterval == Timeout.InfiniteTimeSpan || _devicePollInterval <= TimeSpan.Zero)
            return;

        _devicePollTimer = new System.Timers.Timer(_devicePollInterval.TotalMilliseconds);
        _devicePollTimer.Elapsed += (_, _) => CheckForDeviceChanges();
        _devicePollTimer.AutoReset = true;
        _devicePollTimer.Start();
    }

    private void UpdateKnownDeviceSnapshot()
    {
        var snapshot = GetDeviceSnapshot();
        _lastKnownDeviceSignature = BuildDeviceSignature(snapshot);
        _lastKnownHasDevices = snapshot.Count > 0;
        _lastKnownPreferredDeviceAvailable = IsPreferredDeviceAvailable(snapshot);
        _lastKnownSnapshotInitialized = true;
    }

    internal void CheckForDeviceChanges()
    {
        try
        {
            var snapshot = GetDeviceSnapshot();
            var signature = BuildDeviceSignature(snapshot);
            if (_lastKnownSnapshotInitialized && signature == _lastKnownDeviceSignature)
                return;

            var previousHadDevices = _lastKnownHasDevices;
            var previousPreferredDeviceAvailable = _lastKnownPreferredDeviceAvailable;
            var currentHasDevices = snapshot.Count > 0;
            var currentPreferredDeviceAvailable = IsPreferredDeviceAvailable(snapshot);

            _lastKnownDeviceSignature = signature;
            _lastKnownHasDevices = currentHasDevices;
            _lastKnownPreferredDeviceAvailable = currentPreferredDeviceAvailable;
            _lastKnownSnapshotInitialized = true;

            DevicesChanged?.Invoke(this, EventArgs.Empty);

            if (!currentHasDevices)
            {
                if (_isWarmedUp || _waveIn is not null)
                    HandleDeviceLost();
                return;
            }

            if (_isWarmedUp && !IsActiveDeviceAvailable(snapshot))
            {
                HandleDeviceLost();
                WarmUp();
                if (!previousHadDevices
                    || (!previousPreferredDeviceAvailable && currentPreferredDeviceAvailable))
                {
                    DeviceAvailable?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            if (_isWarmedUp && currentPreferredDeviceAvailable && !IsActiveDevicePreferred())
            {
                DisposeWaveIn();
                WarmUp();
            }
            else if (!_isWarmedUp)
            {
                WarmUp();
            }

            if (!previousHadDevices
                || (!previousPreferredDeviceAvailable && currentPreferredDeviceAvailable))
            {
                DeviceAvailable?.Invoke(this, EventArgs.Empty);
            }
        }
        catch { }
    }

    private void HandleDeviceLost()
    {
        ClearRecordingState();
        DisposeWaveIn();
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRecordingState()
    {
        _isRecording = false;
        lock (_bufferLock)
        {
            _sampleBuffer = null;
        }
    }

    private IReadOnlyList<AudioInputDeviceSnapshot> GetDeviceSnapshot()
    {
        var devices = new List<AudioInputDeviceSnapshot>();
        for (var i = 0; i < _deviceProvider.DeviceCount; i++)
        {
            var name = TryGetDeviceName(i);
            if (name is not null)
                devices.Add(new AudioInputDeviceSnapshot(i, name));
        }

        return devices;
    }

    private static string BuildDeviceSignature(IReadOnlyList<AudioInputDeviceSnapshot> devices) =>
        string.Join('\n', devices.Select(device => $"{device.DeviceNumber}:{device.Name}"));

    private bool IsPreferredDeviceAvailable(IReadOnlyList<AudioInputDeviceSnapshot> devices)
    {
        if (_configuredDeviceNumber is not int configuredDeviceNumber)
            return devices.Count > 0;

        if (!string.IsNullOrWhiteSpace(_configuredDeviceName)
            && devices.Any(device => string.Equals(device.Name, _configuredDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return devices.Any(device => device.DeviceNumber == configuredDeviceNumber);
    }

    private bool IsActiveDeviceAvailable(IReadOnlyList<AudioInputDeviceSnapshot> devices)
    {
        if (_activeDeviceNumber < 0)
            return false;

        var active = devices.FirstOrDefault(device => device.DeviceNumber == _activeDeviceNumber);
        if (active is null)
            return false;

        return string.IsNullOrWhiteSpace(_activeDeviceName)
            || string.Equals(active.Name, _activeDeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsActiveDevicePreferred()
    {
        if (_configuredDeviceNumber is not int configuredDeviceNumber)
            return _activeDeviceNumber >= 0;

        if (!string.IsNullOrWhiteSpace(_configuredDeviceName))
        {
            return string.Equals(_activeDeviceName, _configuredDeviceName, StringComparison.OrdinalIgnoreCase);
        }

        return _activeDeviceNumber == configuredDeviceNumber;
    }

    /// <summary>
    /// Starts preview.
    /// </summary>
    public void StartPreview(int? deviceNumber)
    {
        if (_isRecording)
            return;

        StopPreview();
        if (_disposed || _deviceProvider.DeviceCount == 0) return;

        var deviceIndex = deviceNumber.HasValue
            ? ResolvePreferredDeviceNumber(allowFallback: true)
            : FindBestMicrophoneDevice();
        if (deviceIndex < 0) return;

        try
        {
            _previewWaveIn = _captureFactory.Create(
                deviceIndex,
                new WaveFormat(SampleRate, BitsPerSample, Channels),
                bufferMilliseconds: 50);
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

    /// <summary>
    /// Stops preview.
    /// </summary>
    public void StopPreview()
    {
        if (_previewWaveIn is not null)
        {
            _previewWaveIn.DataAvailable -= OnPreviewDataAvailable;
            StopRecordingForCleanup(_previewWaveIn);
            _previewWaveIn.Dispose();
            _previewWaveIn = null;
        }
        _isPreviewing = false;
    }

    private static void StopRecordingForCleanup(IAudioInputCapture waveIn)
    {
        try
        {
            waveIn.StopRecording();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or MmException)
        {
            System.Diagnostics.Debug.WriteLine($"StopRecording during audio cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets whether is previewing.
    /// </summary>
    public bool IsPreviewing => _isPreviewing;

    private void OnPreviewDataAvailable(object? sender, AudioInputDataAvailableEventArgs e)
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

    private void DisposeWaveIn(bool stopRecording = true)
    {
        if (_waveIn is not null)
        {
            var waveIn = _waveIn;
            _waveIn = null;
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            if (stopRecording)
            {
                StopRecordingForCleanup(waveIn);
            }
            waveIn.Dispose();
        }
        _isWarmedUp = false;
        _activeDeviceNumber = -1;
        _activeDeviceName = null;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
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

    private static IReadOnlyList<(int DeviceNumber, string Name)> GetAvailableDevices(IAudioInputDeviceProvider provider)
    {
        var devices = new List<(int, string)>();
        for (var i = 0; i < provider.DeviceCount; i++)
        {
            devices.Add((i, provider.GetDeviceName(i)));
        }
        return devices;
    }
}

/// <summary>
/// Represents audio level event args data.
/// </summary>
/// <param name="PeakLevel">Peak level supplied to the member.</param>
/// <param name="RmsLevel">Rms level supplied to the member.</param>
public sealed record AudioLevelEventArgs(float PeakLevel, float RmsLevel);

/// <summary>
/// Provides samples available event args behavior.
/// </summary>
public sealed class SamplesAvailableEventArgs(float[] samples) : EventArgs
{
    /// <summary>
    /// Gets the samples.
    /// </summary>
    public float[] Samples { get; } = samples;
}

/// <summary>
/// Represents audio tail snapshot data.
/// </summary>
/// <param name="LastSamplesAvailableUtc">Last samples available utc supplied to the member.</param>
/// <param name="RecentChunks">Recent chunks supplied to the member.</param>
public sealed record AudioTailSnapshot(
    DateTime? LastSamplesAvailableUtc,
    IReadOnlyList<AudioChunkTelemetry> RecentChunks);

/// <summary>
/// Represents audio chunk telemetry data.
/// </summary>
/// <param name="TimestampUtc">Timestamp utc supplied to the member.</param>
/// <param name="Peak">Peak supplied to the member.</param>
/// <param name="Rms">Rms supplied to the member.</param>
/// <param name="PreGainRms">Pre gain rms supplied to the member.</param>
/// <param name="SampleCount">Sample count supplied to the member.</param>
public sealed record AudioChunkTelemetry(
    DateTime TimestampUtc,
    float Peak,
    float Rms,
    float PreGainRms,
    int SampleCount);

internal sealed record AudioInputDeviceSnapshot(int DeviceNumber, string Name);

internal interface IAudioInputDeviceProvider
{
    int DeviceCount { get; }
    string GetDeviceName(int deviceNumber);
}

internal interface IAudioInputCaptureFactory
{
    IAudioInputCapture Create(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds);
}

internal interface IAudioInputCapture : IDisposable
{
    event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

internal sealed class AudioInputDataAvailableEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    /// <summary>
    /// Gets the buffer.
    /// </summary>
    public byte[] Buffer { get; } = buffer;
    /// <summary>
    /// Gets the bytes recorded.
    /// </summary>
    public int BytesRecorded { get; } = bytesRecorded;
}

internal sealed class AudioInputRecordingStoppedEventArgs(Exception? exception = null) : EventArgs
{
    /// <summary>
    /// Gets the exception.
    /// </summary>
    public Exception? Exception { get; } = exception;
}

internal sealed class WaveInAudioInputDeviceProvider : IAudioInputDeviceProvider
{
    /// <summary>
    /// Gets the device count.
    /// </summary>
    public int DeviceCount => WaveInEvent.DeviceCount;

    /// <summary>
    /// Returns device name.
    /// </summary>
    public string GetDeviceName(int deviceNumber) =>
        WaveInEvent.GetCapabilities(deviceNumber).ProductName;
}

internal sealed class WaveInAudioInputCaptureFactory : IAudioInputCaptureFactory
{
    /// <summary>
    /// Creates.
    /// </summary>
    public IAudioInputCapture Create(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds) =>
        new WaveInAudioInputCapture(deviceNumber, waveFormat, bufferMilliseconds);
}

internal sealed class WaveInAudioInputCapture : IAudioInputCapture
{
    private readonly WaveInEvent _waveIn;

    /// <summary>
    /// Performs wave in audio input capture.
    /// </summary>
    public WaveInAudioInputCapture(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = waveFormat,
            BufferMilliseconds = bufferMilliseconds
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// Raised when data available.
    /// </summary>
    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    /// <summary>
    /// Raised when recording stopped.
    /// </summary>
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    /// <summary>
    /// Starts recording.
    /// </summary>
    public void StartRecording() => _waveIn.StartRecording();

    /// <summary>
    /// Stops recording.
    /// </summary>
    public void StopRecording() => _waveIn.StopRecording();

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) =>
        DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(e.Buffer, e.BytesRecorded));

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        RecordingStopped?.Invoke(this, new AudioInputRecordingStoppedEventArgs(e.Exception));
}
