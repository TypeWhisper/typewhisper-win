using System.IO;
using System.Runtime.InteropServices;
using NAudio;
using NAudio.Wave;
using TypeWhisper.Core;
using TypeWhisper.Core.Audio;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Coordinates recorder capture across microphone and system audio sources.
/// </summary>
public sealed class RecorderCaptureService : IStreamingAudioSource, IDisposable
{
    private const int SampleRate = 16000;
    private static readonly TimeSpan StopDrainDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SystemAudioWarningGracePeriod = TimeSpan.FromSeconds(2);

    private readonly AudioRecordingService _microphone;
    private readonly SystemAudioCaptureService _systemAudio;
    private readonly object _lock = new();

    private RecorderCaptureOptions? _options;
    private RecorderTranscriptionBuffer? _buffer;
    private DateTime _startedAtUtc;
    private bool _isRecording;
    private bool _disposed;
    private bool _systemAudioHadSignal;
    private int _lastPublishedSampleCount;
    private System.Timers.Timer? _warningTimer;

    /// <summary>
    /// Initializes a new instance of the RecorderCaptureService class.
    /// </summary>
    public RecorderCaptureService(
        AudioRecordingService microphone,
        SystemAudioCaptureService systemAudio)
    {
        _microphone = microphone;
        _systemAudio = systemAudio;
    }

    /// <summary>
    /// Raised when recorder audio level changes.
    /// </summary>
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    /// <summary>
    /// Raised when normalized recorder samples are available.
    /// </summary>
    public event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;

    /// <summary>
    /// Gets whether the recorder is active.
    /// </summary>
    public bool IsRecording => _isRecording;
    /// <summary>
    /// Gets the current recorder duration.
    /// </summary>
    public TimeSpan Duration => _isRecording ? DateTime.UtcNow - _startedAtUtc : TimeSpan.Zero;
    /// <summary>
    /// Gets the microphone level.
    /// </summary>
    public float MicLevel { get; private set; }
    /// <summary>
    /// Gets the system-audio level.
    /// </summary>
    public float SystemLevel { get; private set; }
    /// <summary>
    /// Gets the peak RMS level for live streaming.
    /// </summary>
    public float PeakRmsLevel => Math.Max(_microphone.PeakRmsLevel, _systemAudio.PeakRmsLevel);
    /// <summary>
    /// Gets a warning message when system audio appears silent.
    /// </summary>
    public string? SystemAudioWarningMessage { get; private set; }

    /// <summary>
    /// Starts recorder capture.
    /// </summary>
    public Task StartAsync(RecorderCaptureOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!options.MicEnabled && !options.SystemAudioEnabled)
            throw new InvalidOperationException(Loc.Instance["Recorder.SelectAtLeastOneSource"]);
        if (_isRecording)
            throw new InvalidOperationException(Loc.Instance["Recorder.AlreadyRecording"]);

        _options = options;
        _buffer = new RecorderTranscriptionBuffer(options.MicDuckingMode);
        _startedAtUtc = DateTime.UtcNow;
        _lastPublishedSampleCount = 0;
        _systemAudioHadSignal = false;
        SystemAudioWarningMessage = null;
        MicLevel = 0;
        SystemLevel = 0;

        if (options.MicEnabled)
        {
            if (!_microphone.WarmUp())
                throw new InvalidOperationException(Loc.Instance["Status.NoMicrophone"]);

            _microphone.AudioLevelChanged += OnMicrophoneLevelChanged;
            _microphone.SamplesAvailable += OnMicrophoneSamplesAvailable;
            _microphone.StartRecording();
        }

        if (options.SystemAudioEnabled)
        {
            _systemAudio.AudioLevelChanged += OnSystemLevelChanged;
            _systemAudio.SamplesAvailable += OnSystemSamplesAvailable;
            _systemAudio.StartCapture(options.SystemAudioDeviceId);
            StartWarningTimer();
        }

        _isRecording = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops recorder capture and writes the configured output file.
    /// </summary>
    public async Task<RecorderCaptureResult?> StopAsync(CancellationToken ct = default)
    {
        if (!_isRecording || _options is null)
            return null;

        var options = _options;
        _isRecording = false;
        StopWarningTimer();

        try
        {
            await Task.Delay(StopDrainDuration, ct);
        }
        catch (OperationCanceledException)
        {
            // Stopping should continue even if the short drain delay is canceled.
        }

        float[] micSamples = [];
        float[] systemSamples = [];

        if (options.MicEnabled)
        {
            _microphone.AudioLevelChanged -= OnMicrophoneLevelChanged;
            _microphone.SamplesAvailable -= OnMicrophoneSamplesAvailable;
            micSamples = await _microphone.StopRecordingAsync(ct) ?? [];
        }

        if (options.SystemAudioEnabled)
        {
            _systemAudio.AudioLevelChanged -= OnSystemLevelChanged;
            _systemAudio.SamplesAvailable -= OnSystemSamplesAvailable;
            systemSamples = _systemAudio.StopCapture();
        }

        var transcriptionSamples = RecorderMixer.Mix(micSamples, systemSamples, options.MicDuckingMode);
        if (transcriptionSamples.Length == 0)
            return null;

        var (outputSamples, channels) = BuildOutputSamples(micSamples, systemSamples, options);
        var filePath = WriteOutputFile(outputSamples, channels, options);
        var durationSamples = options.TrackMode == RecorderTrackMode.Separate
            ? outputSamples.Length / 2
            : outputSamples.Length;

        return new RecorderCaptureResult(
            filePath,
            Path.GetFileName(filePath),
            transcriptionSamples,
            TimeSpan.FromSeconds(durationSamples / (double)SampleRate));
    }

    /// <summary>
    /// Returns the current mono transcription buffer.
    /// </summary>
    public float[]? GetCurrentBuffer()
    {
        var options = _options;
        var buffer = _buffer;
        if (!_isRecording || options is null || buffer is null)
            return null;

        return buffer.GetCurrentBuffer(options.MicEnabled, options.SystemAudioEnabled);
    }

    /// <summary>
    /// Returns recent mono transcription samples.
    /// </summary>
    public float[] GetRecentBuffer(TimeSpan duration)
    {
        var options = _options;
        var buffer = _buffer;
        if (options is null || buffer is null)
            return [];

        return buffer.GetRecentBuffer(duration, options.MicEnabled, options.SystemAudioEnabled);
    }

    /// <summary>
    /// Returns mono transcription samples after a mixed-buffer offset.
    /// </summary>
    public float[] GetBufferDelta(int previousSampleCount)
    {
        var options = _options;
        var buffer = _buffer;
        if (options is null || buffer is null)
            return [];

        return buffer.GetBufferDelta(previousSampleCount, options.MicEnabled, options.SystemAudioEnabled);
    }

    /// <summary>
    /// Returns system-audio output devices available for loopback capture.
    /// </summary>
    public IReadOnlyList<SystemAudioOutputDevice> GetSystemAudioOutputDevices() =>
        _systemAudio.GetAvailableOutputDevices();

    private void OnMicrophoneLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        MicLevel = Math.Min(e.PeakLevel, 1f);
        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(Math.Max(MicLevel, SystemLevel), PeakRmsLevel));
    }

    private void OnSystemLevelChanged(float level)
    {
        SystemLevel = Math.Min(level, 1f);
        if (level > 0.0001f)
        {
            _systemAudioHadSignal = true;
            SystemAudioWarningMessage = null;
        }

        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(Math.Max(MicLevel, SystemLevel), PeakRmsLevel));
    }

    private void OnMicrophoneSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        var buffer = _buffer;
        if (!_isRecording || buffer is null)
            return;

        buffer.AppendMic(e.Samples);
        PublishMixedDelta();
    }

    private void OnSystemSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        var buffer = _buffer;
        if (!_isRecording || buffer is null)
            return;

        buffer.AppendSystem(e.Samples);
        if (e.Samples.Any(sample => Math.Abs(sample) > 0.0001f))
        {
            _systemAudioHadSignal = true;
            SystemAudioWarningMessage = null;
        }
        PublishMixedDelta();
    }

    private void PublishMixedDelta()
    {
        float[] delta;
        lock (_lock)
        {
            delta = GetBufferDelta(_lastPublishedSampleCount);
            if (delta.Length == 0)
                return;

            _lastPublishedSampleCount += delta.Length;
        }

        SamplesAvailable?.Invoke(this, new SamplesAvailableEventArgs(delta));
    }

    private static (float[] Samples, int Channels) BuildOutputSamples(
        float[] micSamples,
        float[] systemSamples,
        RecorderCaptureOptions options) =>
        options.TrackMode == RecorderTrackMode.Separate
            ? (RecorderMixer.InterleaveSeparateTracks(micSamples, systemSamples), 2)
            : (RecorderMixer.MixForOutput(micSamples, systemSamples, options.MicDuckingMode), 1);

    private static string WriteOutputFile(float[] samples, int channels, RecorderCaptureOptions options)
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        var extension = options.OutputFormat == RecorderOutputFormat.M4A ? "m4a" : "wav";
        var fileName = $"recording-{DateTime.Now:yyyy-MM-dd-HHmmssfff}.{extension}";
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            throw new InvalidOperationException("Recorder output file name is invalid.");

        var path = Path.Combine(
            TypeWhisperEnvironment.AudioPath,
            safeFileName);

        if (options.OutputFormat == RecorderOutputFormat.Wav)
        {
            File.WriteAllBytes(path, WavEncoder.Encode(samples, SampleRate, channels));
            return path;
        }

        WriteM4A(path, samples, channels);
        return path;
    }

    private static void WriteM4A(string filePath, float[] samples, int channels)
    {
        try
        {
            using var pcmStream = new MemoryStream(FloatToPcm16(samples));
            using var waveStream = new RawSourceWaveStream(
                pcmStream,
                new WaveFormat(SampleRate, 16, channels));
            MediaFoundationEncoder.EncodeToAac(waveStream, filePath);
        }
        catch (Exception ex) when (ex is MmException
            or COMException
            or InvalidOperationException
            or NotSupportedException)
        {
            throw new InvalidOperationException(Loc.Instance["Recorder.M4AEncoderUnavailable"], ex);
        }
    }

    private static byte[] FloatToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)(clamped * 32767f);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }

    private void StartWarningTimer()
    {
        StopWarningTimer();
        _warningTimer = new System.Timers.Timer(500) { AutoReset = true };
        _warningTimer.Elapsed += (_, _) => EvaluateSystemAudioWarning(DateTime.UtcNow);
        _warningTimer.Start();
    }

    internal void EvaluateSystemAudioWarning(DateTime nowUtc)
    {
        if (!_isRecording || _options?.SystemAudioEnabled != true)
            return;

        if (!_systemAudioHadSignal && nowUtc - _startedAtUtc >= SystemAudioWarningGracePeriod)
            SystemAudioWarningMessage = Loc.Instance["Recorder.SystemAudioSilentWarning"];
    }

    private void StopWarningTimer()
    {
        _warningTimer?.Stop();
        _warningTimer?.Dispose();
        _warningTimer = null;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        StopWarningTimer();
        _isRecording = false;
        _microphone.AudioLevelChanged -= OnMicrophoneLevelChanged;
        _microphone.SamplesAvailable -= OnMicrophoneSamplesAvailable;
        _systemAudio.AudioLevelChanged -= OnSystemLevelChanged;
        _systemAudio.SamplesAvailable -= OnSystemSamplesAvailable;
        _microphone.Dispose();
        _systemAudio.Dispose();

        _disposed = true;
    }
}
