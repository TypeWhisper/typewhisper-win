using NAudio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides audio recording service behavior.
/// </summary>
public sealed class AudioRecordingService : IStreamingAudioSource, IDisposable
{
    private enum CaptureDisposalClassification
    {
        Routine,
        Failure,
        FallbackRecreated
    }

    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private const float AgcTargetRms = 0.1f;
    private const float AgcMaxGain = 20f;
    private const float AgcMinGain = 1f;
    private const float NormalizationTarget = 0.707f;
    private static readonly TimeSpan StopDrainDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FallbackDevicePollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum per-chunk RMS level to consider as containing speech.
    /// Below this threshold, audio is treated as silence.
    /// </summary>
    public const float SpeechEnergyThreshold = 0.01f;

    private readonly IAudioInputDeviceProvider _deviceProvider;
    private readonly IAudioInputCaptureFactory _captureFactory;
    private readonly IAudioInputDeviceChangeNotifier? _deviceChangeNotifier;
    private readonly TimeSpan _devicePollInterval;
    private readonly object _deviceInfoCacheLock = new();
    private readonly object _deviceChangeCheckLock = new();
    private readonly object _captureLifecycleLock = new();
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
    private IReadOnlyList<MicrophonePriorityItem> _microphonePriorityList = [];
    private int _activeDeviceNumber = -1;
    private string? _activeDeviceId;
    private string? _activeDeviceName;
    private float _peakRmsLevel;
    private float _preGainPeakRms;
    private float _currentRmsLevel;
    private System.Timers.Timer? _devicePollTimer;
    private IReadOnlyList<AudioInputDeviceInfo> _cachedDeviceInfos = [];
    private bool _hasDeviceInfoCache;
    private bool _deviceNotificationsStarted;
    private int _deviceChangeRefreshQueued;
    private string _lastKnownDeviceSignature = "";
    private bool _lastKnownHasDevices;
    private bool _lastKnownPreferredDeviceAvailable;
    private bool _lastKnownSnapshotInitialized;
    private bool _deviceLossReported;
    private const int TailDiagnosticChunkLimit = 8;
    private readonly Queue<AudioChunkTelemetry> _recentChunks = new();
    private DateTime? _lastSamplesAvailableUtc;
    private int _diagnosticDataAvailableCount;
    private long _recordingStartTimestamp;
    private long _recordingSequence;
    private long _activeRecordingSequence;
    private long _captureGeneration;
    private long _activeCaptureGeneration;

    /// <summary>
    /// Initializes a new instance of the AudioRecordingService class.
    /// </summary>
    public AudioRecordingService()
        : this(
            new WaveInAudioInputDeviceProvider(),
            new FallbackAudioInputCaptureFactory(
                new WasapiAudioInputCaptureFactory(),
                new WaveInAudioInputCaptureFactory()),
            FallbackDevicePollInterval,
            new WasapiAudioInputDeviceChangeNotifier())
    {
    }

    internal AudioRecordingService(
        IAudioInputDeviceProvider deviceProvider,
        IAudioInputCaptureFactory captureFactory,
        TimeSpan devicePollInterval)
        : this(deviceProvider, captureFactory, devicePollInterval, deviceChangeNotifier: null)
    {
    }

    internal AudioRecordingService(
        IAudioInputDeviceProvider deviceProvider,
        IAudioInputCaptureFactory captureFactory,
        TimeSpan devicePollInterval,
        IAudioInputDeviceChangeNotifier? deviceChangeNotifier)
    {
        _deviceProvider = deviceProvider;
        _captureFactory = captureFactory;
        _devicePollInterval = devicePollInterval;
        _deviceChangeNotifier = deviceChangeNotifier;
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

        ApplyPreferredDeviceChange();
    }

    /// <summary>
    /// Sets preferred microphone devices in fallback order.
    /// </summary>
    public void SetMicrophonePriorityList(IReadOnlyList<MicrophonePriorityItem>? priorityList)
    {
        var normalized = NormalizeMicrophonePriorityList(priorityList ?? []);
        if (MicrophonePriorityListsEqual(_microphonePriorityList, normalized))
            return;

        _microphonePriorityList = normalized;
        ApplyPreferredDeviceChange();
    }

    /// <summary>
    /// Performs warm up.
    /// </summary>
    public bool WarmUp()
    {
        lock (_captureLifecycleLock)
        {
            AudioCaptureDiagnostics.Log(
                $"WarmUp enter warmed={_isWarmedUp} disposed={_disposed} deviceCount={SafeDeviceCount()} sync={SynchronizationContext.Current?.GetType().FullName ?? "<null>"}");
            if (_disposed) return false;
            if (_isWarmedUp && _waveIn is not null) return true;

            if (_deviceProvider.DeviceCount == 0)
            {
                AudioCaptureDiagnostics.Log("WarmUp no devices");
                System.Diagnostics.Debug.WriteLine("WarmUp: No audio input devices available.");
                StartDevicePolling();
                return false;
            }

            _activeDeviceNumber = ResolvePreferredDeviceNumber(allowFallback: true);
            if (_activeDeviceNumber < 0)
            {
                AudioCaptureDiagnostics.Log("WarmUp no active device after resolve");
                StartDevicePolling();
                return false;
            }

            try
            {
                AudioCaptureDiagnostics.Log(
                    $"WarmUp creating capture active={_activeDeviceNumber}:{TryGetDeviceName(_activeDeviceNumber) ?? "<unknown>"}");
                _waveIn = _captureFactory.Create(
                    _activeDeviceNumber,
                    new WaveFormat(SampleRate, BitsPerSample, Channels),
                    bufferMilliseconds: 30);
                _waveIn.Prepare();
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;

                SetActiveDeviceIdentity(_activeDeviceNumber);
                _isWarmedUp = true;
                _activeCaptureGeneration = ++_captureGeneration;
                AudioCaptureDiagnostics.Log(
                    $"WarmUp prepared captureGeneration={_activeCaptureGeneration} reusable={_waveIn.CanRestartAfterStop} active={_activeDeviceNumber}:{_activeDeviceName ?? "<unknown>"} format={DescribeWaveFormat(_waveIn.WaveFormat)}");
            }
            catch (Exception ex) when (IsNonFatalAudioException(ex))
            {
                AudioCaptureDiagnostics.Log($"WarmUp failed {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"WarmUp failed: {ex.Message}");
                DisposeWaveIn(
                    stopRecording: false,
                    reason: "prepare failure",
                    classification: CaptureDisposalClassification.Failure);
            }

            StartDevicePolling();
            return _isWarmedUp;
        }
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
        TryGetDeviceInfos()
            .Select(device => (device.DeviceNumber, device.Name))
            .ToList();

    /// <summary>
    /// Returns available input devices with stable ids when available.
    /// </summary>
    internal IReadOnlyList<AudioInputDeviceInfo> GetAvailableInputDeviceInfos() =>
        TryGetDeviceInfos();

    internal bool TryGetCachedAvailableInputDeviceInfos(
        out IReadOnlyList<AudioInputDeviceInfo> deviceInfos)
    {
        lock (_deviceInfoCacheLock)
        {
            deviceInfos = _cachedDeviceInfos;
            return _hasDeviceInfoCache;
        }
    }

    internal IReadOnlyList<AudioInputDeviceInfo> RefreshAvailableInputDeviceInfos() =>
        TryGetDeviceInfos(refresh: true);

    /// <summary>
    /// Starts recording.
    /// </summary>
    public void StartRecording()
    {
        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_captureLifecycleLock)
        {
            AudioCaptureDiagnostics.Log(
                $"StartRecording enter serviceRecording={_isRecording} warmed={_isWarmedUp} previewing={_isPreviewing} waveIn={_waveIn is not null}");
            if (_isRecording) return;

            // The settings microphone preview uses its own WaveIn instance and can
            // block real capture while the settings window stays open on Dictation.
            // Always stop preview before entering recording mode.
            if (_isPreviewing)
                StopPreview();

            if ((!_isWarmedUp || _waveIn is null) && !WarmUp())
            {
                AudioCaptureDiagnostics.Log("StartRecording warmup failed");
                return;
            }

            if (_waveIn is null)
            {
                AudioCaptureDiagnostics.Log("StartRecording no capture");
                return;
            }

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
            _recordingStartTimestamp = startTimestamp;
            _activeRecordingSequence = ++_recordingSequence;
            _isRecording = true;
            Interlocked.Exchange(ref _diagnosticDataAvailableCount, 0);

            try
            {
                _waveIn.StartRecording();
                AudioCaptureDiagnostics.Log(
                    $"StartRecording active sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} isRecording={_isRecording} format={DescribeWaveFormat(_waveIn.WaveFormat)}");
            }
            catch (Exception ex) when (IsNonFatalAudioException(ex))
            {
                AudioCaptureDiagnostics.Log(
                    $"StartRecording failed sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StartRecording failed: {ex.Message}");
                ClearRecordingState();
                DisposeWaveIn(
                    reason: "start failure",
                    classification: CaptureDisposalClassification.Failure);
            }
        }
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
        lock (_captureLifecycleLock)
        {
            AudioCaptureDiagnostics.Log(
                $"StopRecording enter sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} serviceRecording={_isRecording} waveIn={_waveIn is not null} bufferCount={_sampleBuffer?.Count ?? -1} peak={_peakRmsLevel:F6} preGain={_preGainPeakRms:F6}");
            if (!_isRecording)
                return null;

            if (_waveIn is null)
            {
                AudioCaptureDiagnostics.Log("StopRecording no capture");
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

            if (_waveIn.CanRestartAfterStop)
                StopAndRetainWaveIn(_waveIn);
            else
                DisposeWaveIn(
                    resetWarmUp: false,
                    reason: "fallback recreated",
                    classification: CaptureDisposalClassification.FallbackRecreated);

            if (samples is null || samples.Length == 0)
            {
                AudioCaptureDiagnostics.Log(
                    $"StopRecording returning empty sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration}");
                return null;
            }

            if (NormalizationEnabled)
                NormalizeAudio(samples);

            AudioCaptureDiagnostics.Log(
                $"StopRecording returning sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} samples={samples.Length} duration={samples.Length / 16000.0:F3}");
            return samples;
        }
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
        var capture = _waveIn;
        if (!_isRecording || capture is null || !ReferenceEquals(sender, capture))
            return;

        var decodedSamples = SystemAudioCaptureService.ConvertToTranscriptionSamples(
            e.Buffer,
            e.BytesRecorded,
            capture.WaveFormat);
        var sampleCount = decodedSamples.Length;
        var dataAvailableCount = Interlocked.Increment(ref _diagnosticDataAvailableCount);
        if (dataAvailableCount == 1)
        {
            var startLatency = System.Diagnostics.Stopwatch.GetElapsedTime(_recordingStartTimestamp);
            var startLatencyMilliseconds = startLatency.TotalMilliseconds.ToString(
                "F1",
                System.Globalization.CultureInfo.InvariantCulture);
            AudioCaptureDiagnostics.Log(
                $"FirstDataAvailable sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} startLatencyMs={startLatencyMilliseconds} bytes={e.BytesRecorded} decoded={sampleCount}");
        }
        if (dataAvailableCount <= 5 || dataAvailableCount % 50 == 0)
        {
            AudioCaptureDiagnostics.Log(
                $"DataAvailable accepted sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} count={dataAvailableCount} bytes={e.BytesRecorded} decoded={sampleCount} format={DescribeWaveFormat(capture.WaveFormat)} recording={_isRecording}");
        }
        if (sampleCount == 0) return;

        float agcGain = 1f;

        // Compute pre-gain RMS for speech energy detection (unaffected by AGC)
        float preGainSum = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var s = decodedSamples[i];
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
            var sample = decodedSamples[i];

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

        RaiseAudioLevelChanged(peak, rms);

        if (_sampleBuffer is not null)
            RaiseSamplesAvailable(chunkBuffer);
    }

    private void OnRecordingStopped(object? sender, AudioInputRecordingStoppedEventArgs e)
    {
        lock (_captureLifecycleLock)
        {
            if (_waveIn is null || !ReferenceEquals(sender, _waveIn))
                return;

            var captureFailed = e.Exception is not null;
            var activeDeviceAvailable = IsActiveDeviceAvailable(GetDeviceSnapshot(refresh: true));
            AudioCaptureDiagnostics.Log(
                $"RecordingStopped unexpected sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} captureFailed={captureFailed} activeAvailable={activeDeviceAvailable} exception={e.Exception?.GetType().Name}:{e.Exception?.Message}");

            System.Diagnostics.Debug.WriteLine(captureFailed
                ? $"Audio input capture stopped unexpectedly: {e.Exception!.Message}"
                : "Audio input capture stopped unexpectedly without an exception.");

            ClearRecordingState();
            DisposeWaveIn(
                stopRecording: false,
                reason: "unexpected stop",
                classification: CaptureDisposalClassification.Failure);
            StartDevicePolling();

            if (captureFailed && !activeDeviceAvailable)
                RaiseDeviceLost();
        }
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

        var defaultDeviceNumber = FindSystemDefaultDevice();
        if (defaultDeviceNumber >= 0)
            return defaultDeviceNumber;

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
        var priorityDeviceNumber = FindPriorityDeviceNumber();
        if (priorityDeviceNumber >= 0)
            return priorityDeviceNumber;

        if (_configuredDeviceNumber is int configuredDeviceNumber)
        {
            var configuredDeviceName = TryGetDeviceInfo(configuredDeviceNumber)?.Name
                ?? TryGetDeviceName(configuredDeviceNumber);
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

    private void ApplyPreferredDeviceChange()
    {
        lock (_captureLifecycleLock)
        {
            if (!_isWarmedUp)
                return;

            var newDevice = ResolvePreferredDeviceNumber(allowFallback: true);
            if (!ActiveDeviceMatches(newDevice))
            {
                DisposeWaveIn(reason: "device change");
                if (newDevice >= 0)
                    WarmUp();
            }
            else if (newDevice >= 0)
            {
                _activeDeviceNumber = newDevice;
                SetActiveDeviceIdentity(newDevice);
            }
        }
    }

    private int FindPriorityDeviceNumber()
    {
        if (_microphonePriorityList.Count == 0)
            return -1;

        var devices = GetDeviceSnapshot();
        foreach (var priorityItem in _microphonePriorityList)
        {
            var byId = devices.FirstOrDefault(device =>
                string.Equals(device.Id, priorityItem.Id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId.DeviceNumber;

            var byName = devices.FirstOrDefault(device =>
                DeviceNamesMatch(device.Name, priorityItem.Name));
            if (byName is not null)
                return byName.DeviceNumber;
        }

        return -1;
    }

    private int FindSystemDefaultDevice()
    {
        foreach (var info in TryGetDeviceInfos())
        {
            if (info.IsDefault)
                return info.DeviceNumber;
        }

        string? defaultDeviceName;
        try
        {
            defaultDeviceName = _deviceProvider.GetDefaultDeviceName();
        }
        catch (Exception ex) when (IsNonFatalAudioException(ex))
        {
            AudioCaptureDiagnostics.Log($"Default device lookup failed {ex.GetType().Name}: {ex.Message}");
            return -1;
        }

        if (string.IsNullOrWhiteSpace(defaultDeviceName))
            return -1;

        for (var i = 0; i < _deviceProvider.DeviceCount; i++)
        {
            var currentName = TryGetDeviceName(i);
            if (currentName is not null
                && DeviceNamesMatch(currentName, defaultDeviceName))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindDeviceByName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return -1;

        for (var i = 0; i < _deviceProvider.DeviceCount; i++)
        {
            var currentName = TryGetDeviceName(i);
            if (currentName is not null && DeviceNamesMatch(currentName, deviceName))
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

    private AudioInputDeviceInfo? TryGetDeviceInfo(int deviceNumber)
    {
        if (deviceNumber < 0)
            return null;

        return TryGetDeviceInfos()
            .FirstOrDefault(device => device.DeviceNumber == deviceNumber);
    }

    private IReadOnlyList<AudioInputDeviceInfo> TryGetDeviceInfos(bool refresh = false)
    {
        lock (_deviceInfoCacheLock)
        {
            if (!refresh && _hasDeviceInfoCache)
                return _cachedDeviceInfos;

            try
            {
                _cachedDeviceInfos = [.. _deviceProvider.GetDeviceInfos()];
                _hasDeviceInfoCache = true;
            }
            catch (Exception ex) when (IsNonFatalAudioException(ex))
            {
                AudioCaptureDiagnostics.Log(
                    $"Device info refresh failed {ex.GetType().Name}: {ex.Message}");
            }

            return _cachedDeviceInfos;
        }
    }

    private void SetActiveDeviceIdentity(int deviceNumber)
    {
        var info = TryGetDeviceInfo(deviceNumber);
        _activeDeviceNumber = deviceNumber;
        _activeDeviceId = info?.Id;
        _activeDeviceName = info?.Name ?? TryGetDeviceName(deviceNumber);
    }

    private bool ActiveDeviceMatches(int deviceNumber)
    {
        if (deviceNumber < 0)
            return _activeDeviceNumber < 0;

        if (_activeDeviceNumber == deviceNumber)
        {
            var currentInfo = TryGetDeviceInfo(deviceNumber);
            return currentInfo is null
                || string.IsNullOrWhiteSpace(_activeDeviceId)
                || string.Equals(currentInfo.Id, _activeDeviceId, StringComparison.OrdinalIgnoreCase);
        }

        var newInfo = TryGetDeviceInfo(deviceNumber);
        return newInfo is not null
            && !string.IsNullOrWhiteSpace(_activeDeviceId)
            && string.Equals(newInfo.Id, _activeDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private void StartDevicePolling()
    {
        if (_disposed || _deviceNotificationsStarted || _devicePollTimer is not null)
            return;

        UpdateKnownDeviceSnapshot();

        if (_deviceChangeNotifier is not null)
        {
            _deviceChangeNotifier.DevicesChanged += OnDeviceChangeNotification;
            if (_deviceChangeNotifier.Start())
            {
                _deviceNotificationsStarted = true;
                return;
            }

            _deviceChangeNotifier.DevicesChanged -= OnDeviceChangeNotification;
        }

        if (_devicePollInterval == Timeout.InfiniteTimeSpan || _devicePollInterval <= TimeSpan.Zero)
            return;

        _devicePollTimer = new System.Timers.Timer(_devicePollInterval.TotalMilliseconds);
        _devicePollTimer.Elapsed += (_, _) => CheckForDeviceChanges();
        _devicePollTimer.AutoReset = true;
        _devicePollTimer.Start();
    }

    private void OnDeviceChangeNotification(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        var previousState = Interlocked.CompareExchange(
            ref _deviceChangeRefreshQueued,
            1,
            0);
        if (previousState == 0)
        {
            ThreadPool.QueueUserWorkItem(
                static state => ((AudioRecordingService)state!).ProcessDeviceChangeNotification(),
                this);
            return;
        }

        // State 2 records one follow-up pass. Additional endpoint callbacks
        // arriving before that pass completes are intentionally coalesced.
        Interlocked.CompareExchange(ref _deviceChangeRefreshQueued, 2, 1);
    }

    private void ProcessDeviceChangeNotification()
    {
        while (!_disposed)
        {
            Interlocked.Exchange(ref _deviceChangeRefreshQueued, 1);
            CheckForDeviceChanges();

            if (Interlocked.CompareExchange(
                    ref _deviceChangeRefreshQueued,
                    0,
                    1) == 1)
            {
                return;
            }
        }

        Interlocked.Exchange(ref _deviceChangeRefreshQueued, 0);
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
        lock (_deviceChangeCheckLock)
        {
            try
            {
                var snapshot = GetDeviceSnapshot(refresh: true);
                var signature = BuildDeviceSignature(snapshot);
                if (_lastKnownSnapshotInitialized && signature == _lastKnownDeviceSignature)
                {
                    // The device list is unchanged, but the system default endpoint
                    // may have moved (or a migration was deferred while recording).
                    EnsureActiveDeviceIsPreferred(snapshot);
                    return;
                }

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
                        RaiseDeviceAvailableIfDeviceLossWasReported();
                    }
                    return;
                }

                if (_isWarmedUp && currentPreferredDeviceAvailable && !IsActiveDevicePreferred())
                {
                    EnsureActiveDeviceIsPreferred(snapshot);
                }
                else if (!_isWarmedUp)
                {
                    WarmUp();
                }

                if (!previousHadDevices
                    || (!previousPreferredDeviceAvailable && currentPreferredDeviceAvailable))
                {
                    RaiseDeviceAvailableIfDeviceLossWasReported();
                }
            }
            catch (Exception ex) when (IsNonFatalAudioException(ex))
            {
                AudioCaptureDiagnostics.Log($"Device change check failed {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void EnsureActiveDeviceIsPreferred(IReadOnlyList<AudioInputDeviceSnapshot> snapshot)
    {
        lock (_captureLifecycleLock)
        {
            if (!_isWarmedUp)
                return;

            if (!IsPreferredDeviceAvailable(snapshot) || IsActiveDevicePreferred())
                return;

            if (_isRecording)
            {
                // Never tear down an in-flight recording just to migrate to a
                // preferred device; complete the migration on a later check once
                // recording has stopped.
                return;
            }

            DisposeWaveIn(reason: "device change");
            WarmUp();
        }
    }

    private void RaiseAudioLevelChanged(float peak, float rms) =>
        InvokeEventSafely(AudioLevelChanged, this, new AudioLevelEventArgs(peak, rms), nameof(AudioLevelChanged));

    private void RaiseSamplesAvailable(float[] samples) =>
        InvokeEventSafely(SamplesAvailable, this, new SamplesAvailableEventArgs(samples), nameof(SamplesAvailable));

    private void RaisePreviewLevelChanged(float peak, float rms) =>
        InvokeEventSafely(PreviewLevelChanged, this, new AudioLevelEventArgs(peak, rms), nameof(PreviewLevelChanged));

    private static void InvokeEventSafely<TEventArgs>(
        EventHandler<TEventArgs>? handler,
        object sender,
        TEventArgs args,
        string eventName)
    {
        if (handler is null)
            return;

        foreach (EventHandler<TEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(sender, args);
            }
            catch (Exception ex) when (IsNonFatalAudioException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"{eventName} subscriber failed: {ex.Message}");
                AudioCaptureDiagnostics.Log($"{eventName} subscriber failed {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static bool IsNonFatalAudioException(Exception ex) =>
        NonFatalExceptionFilter.IsNonFatal(ex);

    private int SafeDeviceCount()
    {
        try
        {
            return _deviceProvider.DeviceCount;
        }
        catch (Exception ex) when (IsNonFatalAudioException(ex))
        {
            AudioCaptureDiagnostics.Log($"Device count check failed {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    internal static string DescribeWaveFormat(WaveFormat waveFormat) =>
        $"{waveFormat.Encoding}/{waveFormat.SampleRate}Hz/{waveFormat.BitsPerSample}bit/{waveFormat.Channels}ch";

    private void HandleDeviceLost()
    {
        lock (_captureLifecycleLock)
        {
            ClearRecordingState();
            DisposeWaveIn(
                reason: "device loss",
                classification: CaptureDisposalClassification.Failure);
            RaiseDeviceLost();
        }
    }

    private void RaiseDeviceLost()
    {
        _deviceLossReported = true;
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseDeviceAvailableIfDeviceLossWasReported()
    {
        if (!_deviceLossReported)
            return;

        _deviceLossReported = false;
        DeviceAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRecordingState()
    {
        _isRecording = false;
        lock (_bufferLock)
        {
            _sampleBuffer = null;
        }
    }

    private IReadOnlyList<AudioInputDeviceSnapshot> GetDeviceSnapshot(bool refresh = false)
    {
        var devices = new List<AudioInputDeviceSnapshot>();
        var deviceInfos = TryGetDeviceInfos(refresh);
        if (deviceInfos.Count > 0)
        {
            foreach (var info in deviceInfos)
            {
                devices.Add(new AudioInputDeviceSnapshot(
                    info.DeviceNumber,
                    info.Id,
                    info.Name,
                    info.IsDefault));
            }

            return devices;
        }

        for (var i = 0; i < _deviceProvider.DeviceCount; i++)
        {
            var name = TryGetDeviceName(i);
            if (name is not null)
                devices.Add(new AudioInputDeviceSnapshot(i, StableDeviceIdFromName(name), name, false));
        }

        return devices;
    }

    private static string BuildDeviceSignature(IReadOnlyList<AudioInputDeviceSnapshot> devices) =>
        string.Join('\n', devices.Select(device => $"{device.DeviceNumber}:{device.Id}:{device.Name}"));

    private bool IsPreferredDeviceAvailable(IReadOnlyList<AudioInputDeviceSnapshot> devices)
    {
        if (devices.Count == 0)
            return false;

        if (_microphonePriorityList.Count > 0)
        {
            if (_microphonePriorityList.Any(priorityItem =>
                    devices.Any(device =>
                        string.Equals(device.Id, priorityItem.Id, StringComparison.OrdinalIgnoreCase)
                        || DeviceNamesMatch(device.Name, priorityItem.Name))))
            {
                return true;
            }

            return devices.Count > 0;
        }

        if (_configuredDeviceNumber is not int configuredDeviceNumber)
            return devices.Count > 0;

        if (!string.IsNullOrWhiteSpace(_configuredDeviceName)
            && devices.Any(device => DeviceNamesMatch(device.Name, _configuredDeviceName)))
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

        if (!string.IsNullOrWhiteSpace(_activeDeviceId))
            return string.Equals(active.Id, _activeDeviceId, StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrWhiteSpace(_activeDeviceName)
            || DeviceNamesMatch(active.Name, _activeDeviceName);
    }

    private bool IsActiveDevicePreferred()
    {
        if (_activeDeviceNumber < 0)
            return false;

        var preferredDeviceNumber = ResolvePreferredDeviceNumber(allowFallback: true);
        if (preferredDeviceNumber < 0)
            return true;

        if (_activeDeviceNumber == preferredDeviceNumber)
            return true;

        var preferredInfo = TryGetDeviceInfo(preferredDeviceNumber);
        if (preferredInfo is not null && !string.IsNullOrWhiteSpace(_activeDeviceId))
            return string.Equals(preferredInfo.Id, _activeDeviceId, StringComparison.OrdinalIgnoreCase);

        var preferredName = preferredInfo?.Name ?? TryGetDeviceName(preferredDeviceNumber);
        return !string.IsNullOrWhiteSpace(preferredName)
            && !string.IsNullOrWhiteSpace(_activeDeviceName)
            && DeviceNamesMatch(preferredName, _activeDeviceName);
    }

    /// <summary>
    /// Starts preview.
    /// </summary>
    public void StartPreview(int? deviceNumber)
    {
        lock (_captureLifecycleLock)
        {
            StopPreview();
            if (_isRecording || _disposed || _deviceProvider.DeviceCount == 0) return;

            var deviceIndex = deviceNumber.HasValue
                ? TryGetDeviceName(deviceNumber.Value) is not null
                    ? deviceNumber.Value
                    : ResolvePreferredDeviceNumber(allowFallback: true)
                : FindBestMicrophoneDevice();
            if (deviceIndex < 0) return;

            try
            {
                _previewWaveIn = _captureFactory.Create(
                    deviceIndex,
                    new WaveFormat(SampleRate, BitsPerSample, Channels),
                    bufferMilliseconds: 50);
                _previewWaveIn.DataAvailable += OnPreviewDataAvailable;
                _previewWaveIn.Prepare();
                _previewWaveIn.StartRecording();
                _isPreviewing = true;
            }
            catch (Exception ex) when (IsNonFatalAudioException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"StartPreview failed: {ex.Message}");
                StopPreview();
            }
        }
    }

    /// <summary>
    /// Stops preview.
    /// </summary>
    public void StopPreview()
    {
        lock (_captureLifecycleLock)
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
    }

    private static void StopRecordingForCleanup(IAudioInputCapture waveIn)
    {
        try
        {
            waveIn.StopRecording();
        }
        catch (Exception ex) when (IsNonFatalAudioException(ex))
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
        var capture = _previewWaveIn;
        if (capture is null || !ReferenceEquals(sender, capture))
            return;

        var samples = SystemAudioCaptureService.ConvertToTranscriptionSamples(
            e.Buffer,
            e.BytesRecorded,
            capture.WaveFormat);
        var sampleCount = samples.Length;
        if (sampleCount == 0) return;

        float peak = 0;
        float sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = samples[i];
            var abs = MathF.Abs(sample);
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        var rms = MathF.Sqrt(sumSquares / sampleCount);
        RaisePreviewLevelChanged(peak, rms);
    }

    private void StopAndRetainWaveIn(IAudioInputCapture waveIn)
    {
        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;

        try
        {
            waveIn.StopRecording();
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;
            AudioCaptureDiagnostics.Log(
                $"Capture stopped and retained sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration}");
            AudioCaptureDiagnostics.Log(
                $"DisposeWaveIn release stopRecording=True resetWarmUp=False retained=True sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration}");
        }
        catch (Exception ex) when (IsNonFatalAudioException(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Capture disposed after failure sequence={_activeRecordingSequence} captureGeneration={_activeCaptureGeneration} reason=stop failure exception={ex.GetType().Name}:{ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Reusable audio capture stop failed: {ex.Message}");
            if (ReferenceEquals(_waveIn, waveIn))
                _waveIn = null;
            try
            {
                waveIn.Dispose();
            }
            catch (Exception disposeException) when (IsNonFatalAudioException(disposeException))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Reusable audio capture cleanup failed: {disposeException.Message}");
            }

            _isWarmedUp = false;
            _activeDeviceNumber = -1;
            _activeDeviceId = null;
            _activeDeviceName = null;
            StartDevicePolling();
        }
    }

    private void DisposeWaveIn(
        bool stopRecording = true,
        bool resetWarmUp = true,
        string reason = "cleanup",
        CaptureDisposalClassification classification = CaptureDisposalClassification.Routine)
    {
        lock (_captureLifecycleLock)
        {
            if (_waveIn is not null)
            {
                var waveIn = _waveIn;
                _waveIn = null;
                AudioCaptureDiagnostics.Log(
                    $"DisposeWaveIn release stopRecording={stopRecording} resetWarmUp={resetWarmUp} reason={reason} captureGeneration={_activeCaptureGeneration}");
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.RecordingStopped -= OnRecordingStopped;
                if (stopRecording)
                {
                    StopRecordingForCleanup(waveIn);
                }
                waveIn.Dispose();
                if (classification == CaptureDisposalClassification.Failure)
                {
                    AudioCaptureDiagnostics.Log(
                        $"Capture disposed after failure captureGeneration={_activeCaptureGeneration} reason={reason}");
                }
                else if (classification == CaptureDisposalClassification.FallbackRecreated)
                {
                    AudioCaptureDiagnostics.Log(
                        $"WaveIn fallback released; fallback recreated on next recording captureGeneration={_activeCaptureGeneration}");
                }
            }

            if (!resetWarmUp)
                return;

            _isWarmedUp = false;
            _activeDeviceNumber = -1;
            _activeDeviceId = null;
            _activeDeviceName = null;
        }
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        lock (_captureLifecycleLock)
        {
            if (!_disposed)
            {
                _disposed = true;
                _devicePollTimer?.Dispose();
                _devicePollTimer = null;
                if (_deviceChangeNotifier is not null)
                {
                    if (_deviceNotificationsStarted)
                        _deviceChangeNotifier.DevicesChanged -= OnDeviceChangeNotification;
                    _deviceChangeNotifier.Dispose();
                }
                _isRecording = false;
                StopPreview();
                DisposeWaveIn();
            }
        }
    }

    private static IReadOnlyList<(int DeviceNumber, string Name)> GetAvailableDevices(IAudioInputDeviceProvider provider)
    {
        return GetAvailableDeviceInfos(provider)
            .Select(device => (device.DeviceNumber, device.Name))
            .ToList();
    }

    private static IReadOnlyList<AudioInputDeviceInfo> GetAvailableDeviceInfos(IAudioInputDeviceProvider provider)
    {
        return provider.GetDeviceInfos();
    }

    private static IReadOnlyList<MicrophonePriorityItem> NormalizeMicrophonePriorityList(
        IReadOnlyList<MicrophonePriorityItem> priorityList)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<MicrophonePriorityItem>();
        foreach (var item in priorityList)
        {
            var id = item.Id.Trim();
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                continue;

            var name = item.Name.Trim();
            normalized.Add(new MicrophonePriorityItem(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name));
        }

        return normalized;
    }

    private static bool MicrophonePriorityListsEqual(
        IReadOnlyList<MicrophonePriorityItem> left,
        IReadOnlyList<MicrophonePriorityItem> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal));

    private static bool DeviceNamesMatch(string first, string second) =>
        WasapiAudioInputDeviceOrdering.DeviceNamesMatch(first, second);

    private static string StableDeviceIdFromName(string name) =>
        $"name:{name.Trim().ToUpperInvariant()}";
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

internal sealed record AudioInputDeviceInfo(int DeviceNumber, string Id, string Name, bool IsDefault);

internal sealed record AudioInputDeviceSnapshot(int DeviceNumber, string Id, string Name, bool IsDefault);

internal interface IAudioInputDeviceProvider
{
    int DeviceCount { get; }
    string GetDeviceName(int deviceNumber);
    AudioInputDeviceInfo GetDeviceInfo(int deviceNumber);
    IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos();

    /// <summary>
    /// Returns the friendly name of the system default capture device, or null
    /// when it cannot be determined.
    /// </summary>
    string? GetDefaultDeviceName();
}

internal interface IAudioInputDeviceChangeNotifier : IDisposable
{
    event EventHandler? DevicesChanged;
    bool Start();
}

internal interface IAudioInputCaptureFactory
{
    IAudioInputCapture Create(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds);
}

internal interface IAudioInputCapture : IDisposable
{
    event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
    bool CanRestartAfterStop { get; }
    WaveFormat WaveFormat { get; }
    void Prepare();
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

    public AudioInputDeviceInfo GetDeviceInfo(int deviceNumber)
    {
        var devices = GetDeviceInfos();
        if (deviceNumber < 0 || deviceNumber >= devices.Count)
            throw new ArgumentOutOfRangeException(nameof(deviceNumber));

        return devices[deviceNumber];
    }

    public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos()
    {
        var waveInNames = GetWaveInDeviceNames();
        var devices = WasapiAudioInputDeviceResolver.GetCaptureDevicesInWaveInOrder();
        try
        {
            var defaultDeviceId = WasapiAudioInputDeviceResolver.TryGetDefaultCaptureDeviceId();
            var defaultDeviceName = WasapiAudioInputDeviceResolver.TryGetDefaultCaptureDeviceName();
            var infos = new List<AudioInputDeviceInfo>(waveInNames.Count);

            for (var i = 0; i < waveInNames.Count; i++)
            {
                var waveInName = waveInNames[i];
                if (i < devices.Count)
                {
                    var device = devices[i];
                    var name = device.FriendlyName;
                    infos.Add(new AudioInputDeviceInfo(
                        i,
                        StableWasapiDeviceId(device.ID, name),
                        name,
                        string.Equals(device.ID, defaultDeviceId, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(defaultDeviceName)
                            && WasapiAudioInputDeviceOrdering.DeviceNamesMatch(name, defaultDeviceName))));
                    continue;
                }

                infos.Add(new AudioInputDeviceInfo(
                    i,
                    StableFallbackDeviceId(waveInName),
                    waveInName,
                    !string.IsNullOrWhiteSpace(defaultDeviceName)
                    && WasapiAudioInputDeviceOrdering.DeviceNamesMatch(waveInName, defaultDeviceName)));
            }

            return infos;
        }
        finally
        {
            WasapiAudioInputDeviceResolver.DisposeDevices(devices);
        }
    }

    /// <summary>
    /// Returns the system default capture device name via WASAPI, or null when
    /// no default endpoint is available.
    /// </summary>
    public string? GetDefaultDeviceName() =>
        WasapiAudioInputDeviceResolver.TryGetDefaultCaptureDeviceName();

    private static string StableWasapiDeviceId(string? id, string name) =>
        string.IsNullOrWhiteSpace(id) ? StableFallbackDeviceId(name) : id;

    private static string StableFallbackDeviceId(string name) =>
        $"name:{name.Trim().ToUpperInvariant()}";

    private static IReadOnlyList<string> GetWaveInDeviceNames()
    {
        var count = WaveInEvent.DeviceCount;
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);

        return names;
    }
}

internal sealed class WasapiAudioInputDeviceChangeNotifier : IAudioInputDeviceChangeNotifier, IMMNotificationClient
{
    private readonly object _captureEndpointIdsLock = new();
    private readonly HashSet<string> _captureEndpointIds = new(StringComparer.OrdinalIgnoreCase);
    private MMDeviceEnumerator? _enumerator;

    public event EventHandler? DevicesChanged;

    public bool Start()
    {
        if (_enumerator is not null)
            return true;

        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            RefreshCaptureEndpointIds(enumerator);
            var result = enumerator.RegisterEndpointNotificationCallback(this);
            if (result < 0)
            {
                AudioCaptureDiagnostics.Log(
                    $"Audio endpoint notification registration failed HRESULT=0x{result:X8}");
                ClearCaptureEndpointIds();
                enumerator.Dispose();
                return false;
            }

            _enumerator = enumerator;
            return true;
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Audio endpoint notification registration failed {ex.GetType().Name}: {ex.Message}");
            ClearCaptureEndpointIds();
            enumerator?.Dispose();
            return false;
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (IsCaptureEndpoint(deviceId))
            RaiseDevicesChanged();
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        if (IsCaptureEndpoint(pwstrDeviceId))
            RaiseDevicesChanged();
    }

    public void OnDeviceRemoved(string deviceId)
    {
        if (ForgetCaptureEndpoint(deviceId))
            RaiseDevicesChanged();
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture)
        {
            RememberCaptureEndpoint(defaultDeviceId);
            RaiseDevicesChanged();
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        if (IsRelevantCaptureProperty(key) && IsCaptureEndpoint(pwstrDeviceId))
            RaiseDevicesChanged();
    }

    public void Dispose()
    {
        var enumerator = Interlocked.Exchange(ref _enumerator, null);
        if (enumerator is null)
            return;

        try
        {
            enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Audio endpoint notification unregister failed {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            enumerator.Dispose();
            ClearCaptureEndpointIds();
        }
    }

    internal static bool IsRelevantCaptureProperty(PropertyKey key) =>
        key.Equals(PropertyKeys.PKEY_Device_FriendlyName)
        || key.Equals(PropertyKeys.PKEY_DeviceInterface_FriendlyName)
        || key.Equals(PropertyKeys.PKEY_Device_DeviceDesc)
        || key.Equals(PropertyKeys.PKEY_AudioEngine_DeviceFormat)
        || key.Equals(PropertyKeys.PKEY_AudioEngine_OEMFormat);

    private void RefreshCaptureEndpointIds(MMDeviceEnumerator enumerator)
    {
        var endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All))
        {
            using (device)
                endpointIds.Add(device.ID);
        }

        lock (_captureEndpointIdsLock)
        {
            _captureEndpointIds.Clear();
            _captureEndpointIds.UnionWith(endpointIds);
        }
    }

    private bool IsCaptureEndpoint(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (_captureEndpointIdsLock)
        {
            if (_captureEndpointIds.Contains(deviceId))
                return true;
        }

        var enumerator = Volatile.Read(ref _enumerator);
        if (enumerator is null)
            return false;

        try
        {
            using var device = enumerator.GetDevice(deviceId);
            if (device.DataFlow != DataFlow.Capture)
                return false;

            RememberCaptureEndpoint(deviceId);
            return true;
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Audio endpoint classification failed {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void RememberCaptureEndpoint(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        lock (_captureEndpointIdsLock)
            _captureEndpointIds.Add(deviceId);
    }

    private bool ForgetCaptureEndpoint(string deviceId)
    {
        lock (_captureEndpointIdsLock)
            return _captureEndpointIds.Remove(deviceId);
    }

    private void ClearCaptureEndpointIds()
    {
        lock (_captureEndpointIdsLock)
            _captureEndpointIds.Clear();
    }

    private void RaiseDevicesChanged() =>
        DevicesChanged?.Invoke(this, EventArgs.Empty);
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

    public bool CanRestartAfterStop => false;

    public WaveFormat WaveFormat => _waveIn.WaveFormat;

    public void Prepare()
    {
    }

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

internal sealed class WasapiAudioInputDeviceProvider : IAudioInputDeviceProvider
{
    public int DeviceCount
    {
        get
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count;
        }
    }

    public string GetDeviceName(int deviceNumber)
    {
        var devices = WasapiAudioInputDeviceResolver.GetCaptureDevicesInWaveInOrder();
        try
        {
            if (deviceNumber < 0 || deviceNumber >= devices.Count)
                throw new ArgumentOutOfRangeException(nameof(deviceNumber));

            return devices[deviceNumber].FriendlyName;
        }
        finally
        {
            WasapiAudioInputDeviceResolver.DisposeDevices(devices);
        }
    }

    public AudioInputDeviceInfo GetDeviceInfo(int deviceNumber)
    {
        var devices = GetDeviceInfos();
        if (deviceNumber < 0 || deviceNumber >= devices.Count)
            throw new ArgumentOutOfRangeException(nameof(deviceNumber));

        return devices[deviceNumber];
    }

    public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos()
    {
        var devices = WasapiAudioInputDeviceResolver.GetCaptureDevicesInWaveInOrder();
        try
        {
            var defaultDeviceId = WasapiAudioInputDeviceResolver.TryGetDefaultCaptureDeviceId();
            return devices
                .Select((device, index) => new AudioInputDeviceInfo(
                    index,
                    string.IsNullOrWhiteSpace(device.ID)
                        ? $"name:{device.FriendlyName.Trim().ToUpperInvariant()}"
                        : device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultDeviceId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        finally
        {
            WasapiAudioInputDeviceResolver.DisposeDevices(devices);
        }
    }

    public string? GetDefaultDeviceName() =>
        WasapiAudioInputDeviceResolver.TryGetDefaultCaptureDeviceName();
}

internal sealed class WasapiAudioInputCaptureFactory : IAudioInputCaptureFactory
{
    public IAudioInputCapture Create(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds)
    {
        var devices = WasapiAudioInputDeviceResolver.GetCaptureDevicesInWaveInOrder();
        try
        {
            if (deviceNumber < 0 || deviceNumber >= devices.Count)
                throw new ArgumentOutOfRangeException(nameof(deviceNumber));

            var selectedDevice = devices[deviceNumber];
            devices.RemoveAt(deviceNumber);

            return new WasapiAudioInputCapture(selectedDevice, bufferMilliseconds);
        }
        finally
        {
            WasapiAudioInputDeviceResolver.DisposeDevices(devices);
        }
    }
}

internal sealed class FallbackAudioInputCaptureFactory(
    IAudioInputCaptureFactory primaryFactory,
    IAudioInputCaptureFactory fallbackFactory) : IAudioInputCaptureFactory
{
    public IAudioInputCapture Create(
        int deviceNumber,
        WaveFormat waveFormat,
        int bufferMilliseconds) =>
        new FallbackAudioInputCapture(
            primaryFactory,
            fallbackFactory,
            deviceNumber,
            waveFormat,
            bufferMilliseconds);
}

internal sealed class FallbackAudioInputCapture : IAudioInputCapture
{
    private readonly IAudioInputCaptureFactory _fallbackFactory;
    private readonly int _deviceNumber;
    private readonly WaveFormat _requestedWaveFormat;
    private readonly int _bufferMilliseconds;
    private IAudioInputCapture? _capture;
    private bool _usingFallback;
    private bool _disposed;

    public FallbackAudioInputCapture(
        IAudioInputCaptureFactory primaryFactory,
        IAudioInputCaptureFactory fallbackFactory,
        int deviceNumber,
        WaveFormat waveFormat,
        int bufferMilliseconds)
    {
        _fallbackFactory = fallbackFactory;
        _deviceNumber = deviceNumber;
        _requestedWaveFormat = waveFormat;
        _bufferMilliseconds = bufferMilliseconds;

        try
        {
            _capture = primaryFactory.Create(deviceNumber, waveFormat, bufferMilliseconds);
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Primary microphone capture creation failed; using fallback {ex.GetType().Name}: {ex.Message}");
            _usingFallback = true;
            _capture = fallbackFactory.Create(deviceNumber, waveFormat, bufferMilliseconds);
        }

        AttachCapture();
    }

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public bool CanRestartAfterStop => _capture?.CanRestartAfterStop ?? false;

    public WaveFormat WaveFormat =>
        _capture?.WaveFormat ?? throw new ObjectDisposedException(nameof(FallbackAudioInputCapture));

    public void Prepare()
    {
        ThrowIfDisposed();

        try
        {
            _capture!.Prepare();
        }
        catch (Exception ex) when (!_usingFallback && NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Primary microphone capture prepare failed; using fallback {ex.GetType().Name}: {ex.Message}");
            SwitchToFallback();
            _capture!.Prepare();
        }
    }

    public void StartRecording()
    {
        ThrowIfDisposed();

        try
        {
            _capture!.StartRecording();
        }
        catch (Exception ex) when (!_usingFallback && NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Primary microphone capture start failed; using fallback {ex.GetType().Name}: {ex.Message}");
            SwitchToFallback();
            _capture!.StartRecording();
        }
    }

    public void StopRecording()
    {
        ThrowIfDisposed();
        _capture!.StopRecording();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var capture = _capture;
        _capture = null;
        if (capture is null)
            return;

        DetachCapture(capture);
        capture.Dispose();
    }

    private void SwitchToFallback()
    {
        var primaryCapture = _capture!;
        _capture = null;
        DetachCapture(primaryCapture);
        try
        {
            primaryCapture.Dispose();
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Primary microphone capture cleanup failed {ex.GetType().Name}: {ex.Message}");
        }

        _usingFallback = true;
        try
        {
            _capture = _fallbackFactory.Create(
                _deviceNumber,
                _requestedWaveFormat,
                _bufferMilliseconds);
            AttachCapture();
            AudioCaptureDiagnostics.Log(
                $"WaveIn fallback recreated deviceNumber={_deviceNumber} bufferMs={_bufferMilliseconds}");
        }
        catch
        {
            var failedCapture = _capture;
            _capture = null;
            _disposed = true;
            if (failedCapture is not null)
            {
                try
                {
                    DetachCapture(failedCapture);
                    failedCapture.Dispose();
                }
                catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
                {
                    AudioCaptureDiagnostics.Log(
                        $"Fallback microphone capture cleanup failed {ex.GetType().Name}: {ex.Message}");
                }
            }

            throw;
        }
    }

    private void AttachCapture()
    {
        _capture!.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    private void DetachCapture(IAudioInputCapture capture)
    {
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
    }

    private void OnDataAvailable(object? sender, AudioInputDataAvailableEventArgs e) =>
        DataAvailable?.Invoke(this, e);

    private void OnRecordingStopped(object? sender, AudioInputRecordingStoppedEventArgs e) =>
        RecordingStopped?.Invoke(this, e);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FallbackAudioInputCapture));
    }
}

internal static class WasapiAudioInputDeviceResolver
{
    public static string? TryGetDefaultCaptureDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                return null;

            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.FriendlyName;
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Default capture endpoint lookup failed {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static string? TryGetDefaultCaptureDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                return null;

            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.ID;
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"Default capture endpoint id lookup failed {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static List<MMDevice> GetCaptureDevicesInWaveInOrder()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();
        var order = WasapiAudioInputDeviceOrdering.BuildWaveInCompatibleOrder(
            devices.Select(device => device.FriendlyName).ToArray(),
            GetWaveInDeviceNames());

        return order.Select(index => devices[index]).ToList();
    }

    public static void DisposeDevices(IEnumerable<MMDevice> devices)
    {
        foreach (var device in devices)
            device.Dispose();
    }

    private static string[] GetWaveInDeviceNames()
    {
        var count = WaveInEvent.DeviceCount;
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = WaveInEvent.GetCapabilities(i).ProductName;

        return names;
    }
}

internal static class WasapiAudioInputDeviceOrdering
{
    public static IReadOnlyList<int> BuildWaveInCompatibleOrder(
        IReadOnlyList<string> wasapiDeviceNames,
        IReadOnlyList<string> waveInDeviceNames)
    {
        var remainingIndexes = Enumerable.Range(0, wasapiDeviceNames.Count).ToList();
        var orderedIndexes = new List<int>(wasapiDeviceNames.Count);

        foreach (var remainingIndex in waveInDeviceNames.Select(waveInDeviceName =>
                     remainingIndexes.FindIndex(index =>
                         DeviceNamesMatch(wasapiDeviceNames[index], waveInDeviceName)))
                     .Where(remainingIndex => remainingIndex >= 0))
        {
            orderedIndexes.Add(remainingIndexes[remainingIndex]);
            remainingIndexes.RemoveAt(remainingIndex);
        }

        orderedIndexes.AddRange(remainingIndexes);
        return orderedIndexes;
    }

    internal static bool DeviceNamesMatch(string wasapiDeviceName, string waveInDeviceName)
    {
        if (string.Equals(wasapiDeviceName, waveInDeviceName, StringComparison.OrdinalIgnoreCase))
            return true;

        var trimmedWaveInName = waveInDeviceName.Trim();
        return wasapiDeviceName.StartsWith(trimmedWaveInName, StringComparison.OrdinalIgnoreCase)
            || trimmedWaveInName.StartsWith(wasapiDeviceName, StringComparison.OrdinalIgnoreCase);
    }
}
