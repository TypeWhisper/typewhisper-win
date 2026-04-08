using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Captures system audio output (what you hear) via WASAPI Loopback.
/// Can mix with microphone input for combined capture.
/// </summary>
public sealed class SystemAudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly List<float> _samples = [];
    private bool _isRecording;

    public bool IsRecording => _isRecording;
    public event Action<float>? AudioLevelChanged;

    /// <summary>
    /// Starts capturing system audio output.
    /// </summary>
    public void StartCapture()
    {
        if (_isRecording) return;

        _capture = new WasapiLoopbackCapture();
        _samples.Clear();

        _capture.DataAvailable += (_, e) =>
        {
            // Convert to float samples
            var buffer = e.Buffer;
            var bytesRecorded = e.BytesRecorded;
            var samplesCount = bytesRecorded / 4; // 32-bit float
            var waveFormat = _capture.WaveFormat;

            for (var i = 0; i < bytesRecorded; i += 4)
            {
                if (i + 4 <= bytesRecorded)
                {
                    var sample = BitConverter.ToSingle(buffer, i);
                    _samples.Add(sample);
                }
            }

            // Report level
            if (bytesRecorded > 0)
            {
                float max = 0;
                for (var i = 0; i < bytesRecorded; i += 4)
                {
                    if (i + 4 <= bytesRecorded)
                    {
                        var abs = Math.Abs(BitConverter.ToSingle(buffer, i));
                        if (abs > max) max = abs;
                    }
                }
                AudioLevelChanged?.Invoke(max);
            }
        };

        _capture.RecordingStopped += (_, _) => { _isRecording = false; };

        _capture.StartRecording();
        _isRecording = true;
    }

    /// <summary>
    /// Stops capturing and returns the captured samples resampled to 16kHz mono.
    /// </summary>
    public float[] StopCapture()
    {
        if (!_isRecording || _capture is null) return [];

        _capture.StopRecording();
        _isRecording = false;

        var sourceSampleRate = _capture.WaveFormat.SampleRate;
        var sourceChannels = _capture.WaveFormat.Channels;

        _capture.Dispose();
        _capture = null;

        // Downmix to mono if stereo
        var mono = sourceChannels > 1 ? DownmixToMono(_samples, sourceChannels) : [.. _samples];

        // Resample to 16kHz
        if (sourceSampleRate != 16000)
            mono = Resample(mono, sourceSampleRate, 16000);

        return [.. mono];
    }

    private static List<float> DownmixToMono(List<float> samples, int channels)
    {
        var mono = new List<float>(samples.Count / channels);
        for (var i = 0; i + channels <= samples.Count; i += channels)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
                sum += samples[i + c];
            mono.Add(sum / channels);
        }
        return mono;
    }

    private static List<float> Resample(List<float> samples, int fromRate, int toRate)
    {
        var ratio = (double)toRate / fromRate;
        var outputLength = (int)(samples.Count * ratio);
        var output = new List<float>(outputLength);

        for (var i = 0; i < outputLength; i++)
        {
            var srcIndex = i / ratio;
            var idx = (int)srcIndex;
            var frac = (float)(srcIndex - idx);

            if (idx + 1 < samples.Count)
                output.Add(samples[idx] * (1 - frac) + samples[idx + 1] * frac);
            else if (idx < samples.Count)
                output.Add(samples[idx]);
        }

        return output;
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
