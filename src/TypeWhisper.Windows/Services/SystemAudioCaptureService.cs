using System.Buffers.Binary;
using System.Diagnostics;
using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Captures system audio output (what you hear) via WASAPI Loopback.
/// Can mix with microphone input for combined capture.
/// </summary>
public sealed class SystemAudioCaptureService : IDisposable
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly ISystemAudioLoopbackCaptureFactory _captureFactory;
    private ISystemAudioLoopbackCapture? _capture;
    private readonly List<float> _samples = [];
    private readonly object _lock = new();
    private bool _isRecording;
    private float _peakRmsLevel;
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _startedAt;
    private TimeSpan _timelineOffset;
    private int? _stopSample;
    private const int MaximumSamples = 16000 * 60 * 60;

    /// <summary>
    /// Initializes a new instance of the SystemAudioCaptureService class.
    /// </summary>
    public SystemAudioCaptureService()
        : this(new WasapiLoopbackCaptureFactory())
    {
    }

    internal SystemAudioCaptureService(ISystemAudioLoopbackCaptureFactory captureFactory, Func<TimeSpan>? clock = null)
    {
        _captureFactory = captureFactory;
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    }

    /// <summary>
    /// Gets whether recording is currently active.
    /// </summary>
    public bool IsRecording => _isRecording;
    /// <summary>Whether native capture resources remain owned, including after a failed release.</summary>
    public bool HasCaptureResources => _capture is not null;
    /// <summary>
    /// Gets the peak rms level.
    /// </summary>
    public float PeakRmsLevel => _peakRmsLevel;
    /// <summary>
    /// Raised when audio level changes.
    /// </summary>
    public event Action<float>? AudioLevelChanged;
    /// <summary>
    /// Raised when normalized 16 kHz mono samples are available.
    /// </summary>
    public event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;

    /// <summary>
    /// Starts capturing system audio output.
    /// </summary>
    /// <param name="deviceId">The output device, or the default output when omitted.</param>
    /// <param name="timelineOffset">Elapsed time on a shared recording timeline before this source starts.</param>
    public void StartCapture(string? deviceId = null, TimeSpan timelineOffset = default)
    {
        if (_isRecording) return;

        var startedAt = _clock();
        _capture = _captureFactory.Create(deviceId);
        lock (_lock)
        {
            _samples.Clear();
            _timelineOffset = timelineOffset;
            _startedAt = startedAt;
            _stopSample = null;
        }
        _peakRmsLevel = 0;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _isRecording = true;
        try { _capture.StartRecording(); }
        catch { _isRecording = false; throw; }
    }

    /// <summary>
    /// Returns active output devices that can be used for loopback capture.
    /// </summary>
    public IReadOnlyList<SystemAudioOutputDevice> GetAvailableOutputDevices() =>
        _captureFactory.GetAvailableDevices();

    /// <summary>
    /// Stops capturing and returns the captured samples resampled to 16kHz mono.
    /// </summary>
    /// <param name="timelineEnd">An optional shared stop time, excluding time spent draining other sources.</param>
    public float[] StopCapture(TimeSpan? timelineEnd = null)
    {
        if (_capture is null) return [];

        var capture = _capture;
        lock (_lock) { _stopSample ??= timelineEnd is { } end ? ToSample(end) : TimelineSample(_clock()); }
        try { capture.StopRecording(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Debug.WriteLine($"Stopping system audio failed; releasing capture: {ex.Message}"); }
        finally
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            // A failed release retains the reference and active state so the owner can retry it.
            capture.Dispose();
            _capture = null;
            _isRecording = false;
        }

        lock (_lock)
        {
            PadTo(_stopSample.Value);
            return [.. _samples];
        }
    }

    /// <summary>
    /// Returns current buffer.
    /// </summary>
    public float[]? GetCurrentBuffer()
    {
        if (!_isRecording)
            return null;

        lock (_lock)
        {
            return [.. _samples];
        }
    }

    private void OnDataAvailable(object? sender, AudioInputDataAvailableEventArgs e)
    {
        var receivedAt = _clock();
        var capture = _capture;
        if (!_isRecording || capture is null)
            return;

        var samples = ConvertToTranscriptionSamples(e.Buffer, e.BytesRecorded, capture.WaveFormat);
        if (samples.Length == 0)
            return;

        float peak = 0;
        float sumSquares = 0;
        foreach (var sample in samples)
        {
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        var rms = MathF.Sqrt(sumSquares / samples.Length);
        if (rms > _peakRmsLevel)
            _peakRmsLevel = rms;

        lock (_lock)
        {
            // NAudio exposes callback receipt time, not WASAPI device position. Keep short scheduling
            // jitter contiguous, but preserve real loopback silence instead of compressing the timeline.
            var packetStart = Math.Max(0, TimelineSample(receivedAt) - samples.Length);
            var previousCount = _samples.Count;
            if (_samples.Count == 0 || packetStart - _samples.Count > 800) PadTo(packetStart);
            var count = Math.Min(samples.Length, (_stopSample ?? MaximumSamples) - _samples.Count);
            if (count > 0) _samples.AddRange(samples.AsSpan(0, count).ToArray());
            samples = _samples.GetRange(previousCount, _samples.Count - previousCount).ToArray();
        }

        AudioLevelChanged?.Invoke(peak);
        SamplesAvailable?.Invoke(this, new SamplesAvailableEventArgs(samples));
    }

    private int TimelineSample(TimeSpan now) => ToSample(now - _startedAt + _timelineOffset);

    private static int ToSample(TimeSpan elapsed) => (int)Math.Clamp(
        Math.Round(elapsed.TotalSeconds * 16000), 0, MaximumSamples);

    private void PadTo(int sample)
    {
        var end = Math.Min(sample, _stopSample ?? MaximumSamples);
        if (end > _samples.Count) _samples.AddRange(new float[end - _samples.Count]);
    }

    private void OnRecordingStopped(object? sender, AudioInputRecordingStoppedEventArgs e)
    {
        _isRecording = false;
    }

    /// <summary>
    /// Converts WASAPI input data to normalized 16 kHz mono float samples.
    /// </summary>
    internal static float[] ConvertToTranscriptionSamples(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat waveFormat)
    {
        if (bytesRecorded <= 0 || waveFormat.Channels <= 0 || waveFormat.SampleRate <= 0)
            return [];

        var source = DecodeToMono(buffer, bytesRecorded, waveFormat);
        if (source.Count == 0)
            return [];

        return waveFormat.SampleRate == 16000
            ? source.ToArray()
            : Resample(source, waveFormat.SampleRate, 16000);
    }

    private static List<float> DecodeToMono(byte[] buffer, int bytesRecorded, WaveFormat waveFormat)
    {
        var channels = waveFormat.Channels;
        var bytesPerSample = Math.Max(1, waveFormat.BitsPerSample / 8);
        var frameSize = bytesPerSample * channels;
        if (frameSize <= 0)
            return [];

        var frameCount = bytesRecorded / frameSize;
        var mono = new List<float>(frameCount);
        for (var frame = 0; frame < frameCount; frame++)
        {
            float sum = 0;
            var frameOffset = frame * frameSize;
            for (var channel = 0; channel < channels; channel++)
            {
                var sampleOffset = frameOffset + channel * bytesPerSample;
                sum += DecodeSample(buffer.AsSpan(sampleOffset, bytesPerSample), waveFormat);
            }

            mono.Add(Math.Clamp(sum / channels, -1f, 1f));
        }

        return mono;
    }

    private static float DecodeSample(ReadOnlySpan<byte> sampleBytes, WaveFormat waveFormat)
    {
        if (waveFormat is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == IeeeFloatSubFormat && waveFormat.BitsPerSample == 32)
                return Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(sampleBytes), -1f, 1f);

            if (extensible.SubFormat == PcmSubFormat)
                return DecodePcmSample(sampleBytes, waveFormat.BitsPerSample);
        }

        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && waveFormat.BitsPerSample == 32)
            return Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(sampleBytes), -1f, 1f);

        if (waveFormat.Encoding == WaveFormatEncoding.Pcm)
            return DecodePcmSample(sampleBytes, waveFormat.BitsPerSample);

        return waveFormat.BitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(sampleBytes) / 32768f,
            32 => Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(sampleBytes), -1f, 1f),
            _ => 0f
        };
    }

    private static float DecodePcmSample(ReadOnlySpan<byte> sampleBytes, int bitsPerSample) =>
        bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(sampleBytes) / 32768f,
            24 => DecodeInt24(sampleBytes) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sampleBytes) / 2147483648f,
            _ => 0f
        };

    private static int DecodeInt24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        if ((value & 0x800000) != 0)
            value |= unchecked((int)0xFF000000);
        return value;
    }

    private static float[] Resample(IReadOnlyList<float> samples, int fromRate, int toRate)
    {
        var ratio = (double)toRate / fromRate;
        var outputLength = (int)Math.Floor(samples.Count * ratio);
        if (outputLength <= 0)
            return [];

        var output = new float[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            var srcIndex = i / ratio;
            var idx = (int)srcIndex;
            var frac = (float)(srcIndex - idx);

            output[i] = idx + 1 < samples.Count
                ? Math.Clamp(samples[idx] * (1 - frac) + samples[idx + 1] * frac, -1f, 1f)
                : Math.Clamp(samples[Math.Min(idx, samples.Count - 1)], -1f, 1f);
        }

        return output;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_capture is not null)
        {
            try
            {
                if (_isRecording)
                    _capture.StopRecording();
            }
            catch (MmException ex)
            {
                Debug.WriteLine($"Stopping system audio capture during dispose failed: {ex.Message}");
            }

            _capture.Dispose();
            _capture = null;
        }
        _isRecording = false;
    }
}

internal interface ISystemAudioLoopbackCaptureFactory
{
    IReadOnlyList<SystemAudioOutputDevice> GetAvailableDevices();
    ISystemAudioLoopbackCapture Create(string? deviceId);
}

internal interface ISystemAudioLoopbackCapture : IDisposable
{
    event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
    WaveFormat WaveFormat { get; }
    void StartRecording();
    void StopRecording();
}

internal sealed class WasapiLoopbackCaptureFactory : ISystemAudioLoopbackCaptureFactory
{
    private const string CaptureDevicePrefix = "capture:";

    public IReadOnlyList<SystemAudioOutputDevice> GetAvailableDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<SystemAudioOutputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (device)
                if (IsSystemAudioCaptureMix(device)) devices.Add(new(CaptureDevicePrefix + device.ID, device.FriendlyName));
        return devices;
    }

    public ISystemAudioLoopbackCapture Create(string? deviceId)
    {
        MMDevice? selected = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var useDefault = string.IsNullOrWhiteSpace(deviceId);
            var captureMix = !useDefault && deviceId!.StartsWith(CaptureDevicePrefix, StringComparison.OrdinalIgnoreCase);
            selected = useDefault ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(captureMix ? deviceId![CaptureDevicePrefix.Length..] : deviceId!);
            if (selected.State != DeviceState.Active) throw new InvalidOperationException("The selected audio endpoint is not active.");
            return new WasapiCaptureAdapter(captureMix ? new WasapiCapture(selected) : new WasapiLoopbackCapture(selected), selected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            selected?.Dispose();
            throw new InvalidOperationException("The selected system audio device is unavailable. Reconnect it or choose another device in Recorder settings. No fallback output was selected.", ex);
        }
    }

    private static bool IsSystemAudioCaptureMix(MMDevice device)
    {
        var name = device.FriendlyName;
        return !name.StartsWith("Microphone", StringComparison.CurrentCultureIgnoreCase)
            && (name.Contains("Mix", StringComparison.CurrentCultureIgnoreCase)
                || name.Contains("Virtual Audio", StringComparison.CurrentCultureIgnoreCase)
                || name.Contains("Loopback", StringComparison.CurrentCultureIgnoreCase)
                || name.Contains("Stereo Mix", StringComparison.CurrentCultureIgnoreCase));
    }
}

internal sealed class WasapiCaptureAdapter : ISystemAudioLoopbackCapture
{
    private readonly IWaveIn _capture;
    private IDisposable? _ownedDevice;
    private bool _captureDisposed;

    public WasapiCaptureAdapter(IWaveIn capture, IDisposable? ownedDevice = null)
    {
        _capture = capture;
        _ownedDevice = ownedDevice;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat => _capture.WaveFormat;

    public void StartRecording() { ObjectDisposedException.ThrowIf(_captureDisposed, this); _capture.StartRecording(); }

    public void StopRecording() { if (!_captureDisposed) _capture.StopRecording(); }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        if (!_captureDisposed)
        {
            _capture.Dispose();
            _captureDisposed = true;
        }
        // NAudio releases its AudioClient but does not own the supplied MMDevice.
        // Only release the endpoint after native capture has drained; retain it on a failed release.
        _ownedDevice?.Dispose();
        _ownedDevice = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) =>
        DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(e.Buffer, e.BytesRecorded));

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        RecordingStopped?.Invoke(this, new AudioInputRecordingStoppedEventArgs(e.Exception));
}
