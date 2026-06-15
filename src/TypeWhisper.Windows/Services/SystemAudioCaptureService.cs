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

    /// <summary>
    /// Initializes a new instance of the SystemAudioCaptureService class.
    /// </summary>
    public SystemAudioCaptureService()
        : this(new WasapiLoopbackCaptureFactory())
    {
    }

    internal SystemAudioCaptureService(ISystemAudioLoopbackCaptureFactory captureFactory)
    {
        _captureFactory = captureFactory;
    }

    /// <summary>
    /// Gets whether recording is currently active.
    /// </summary>
    public bool IsRecording => _isRecording;
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
    public void StartCapture(string? deviceId = null)
    {
        if (_isRecording) return;

        _capture = _captureFactory.Create(deviceId);
        lock (_lock)
        {
            _samples.Clear();
        }
        _peakRmsLevel = 0;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _capture.StartRecording();
        _isRecording = true;
    }

    /// <summary>
    /// Returns active output devices that can be used for loopback capture.
    /// </summary>
    public IReadOnlyList<SystemAudioOutputDevice> GetAvailableOutputDevices() =>
        _captureFactory.GetAvailableDevices();

    /// <summary>
    /// Stops capturing and returns the captured samples resampled to 16kHz mono.
    /// </summary>
    public float[] StopCapture()
    {
        if (!_isRecording || _capture is null) return [];

        _capture.StopRecording();
        _isRecording = false;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;

        _capture.Dispose();
        _capture = null;

        lock (_lock)
        {
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
            _samples.AddRange(samples);
        }

        AudioLevelChanged?.Invoke(peak);
        SamplesAvailable?.Invoke(this, new SamplesAvailableEventArgs(samples));
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
        var renderDevices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new SystemAudioOutputDevice(device.ID, device.FriendlyName))
            .ToList();
        var captureMixDevices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Where(IsSystemAudioCaptureMix)
            .Select(device => new SystemAudioOutputDevice(
                CaptureDevicePrefix + device.ID,
                device.FriendlyName))
            .ToList();

        return [.. renderDevices.Concat(captureMixDevices)];
    }

    public ISystemAudioLoopbackCapture Create(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return new WasapiCaptureAdapter(new WasapiLoopbackCapture());

        using var enumerator = new MMDeviceEnumerator();
        if (deviceId.StartsWith(CaptureDevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var captureDevice = enumerator.GetDevice(deviceId[CaptureDevicePrefix.Length..]);
            return new WasapiCaptureAdapter(new WasapiCapture(captureDevice));
        }

        var renderDevice = enumerator.GetDevice(deviceId);
        return new WasapiCaptureAdapter(new WasapiLoopbackCapture(renderDevice));
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
    private readonly WasapiCapture _capture;

    public WasapiCaptureAdapter(WasapiCapture capture)
    {
        _capture = capture;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat => _capture.WaveFormat;

    public void StartRecording() => _capture.StartRecording();

    public void StopRecording() => _capture.StopRecording();

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) =>
        DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(e.Buffer, e.BytesRecorded));

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        RecordingStopped?.Invoke(this, new AudioInputRecordingStoppedEventArgs(e.Exception));
}
