using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TypeWhisper.WinUIPrototype;

internal sealed class PrototypeAudioLevelSource : IDisposable
{
    private readonly Stopwatch _callbackClock = Stopwatch.StartNew();
    private WasapiCapture? _capture;
    private double _latestDbfs = -60;
    private double _maxCallbackGapMilliseconds;
    private long _lastCallbackTimestamp;
    private long _callbackCount;

    internal bool IsCapturing => _capture?.CaptureState == CaptureState.Capturing;
    internal double LatestDbfs => Volatile.Read(ref _latestDbfs);
    internal double MaxCallbackGapMilliseconds => Volatile.Read(ref _maxCallbackGapMilliseconds);
    internal long CallbackCount => Interlocked.Read(ref _callbackCount);
    internal string? Error { get; private set; }

    internal bool TryStart()
    {
        try
        {
            _capture = new WasapiCapture();
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                    Error = args.Exception.Message;
            };
            _capture.StartRecording();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error = exception.Message;
            Dispose();
            return false;
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_capture is null || args.BytesRecorded <= 0)
            return;

        var now = _callbackClock.ElapsedTicks;
        var previous = Interlocked.Exchange(ref _lastCallbackTimestamp, now);
        if (previous > 0)
        {
            var gap = (now - previous) * 1000d / Stopwatch.Frequency;
            var currentMax = Volatile.Read(ref _maxCallbackGapMilliseconds);
            if (gap > currentMax)
                Volatile.Write(ref _maxCallbackGapMilliseconds, gap);
        }

        var format = _capture.WaveFormat;
        var sumSquares = 0d;
        var sampleCount = 0;
        var span = args.Buffer.AsSpan(0, args.BytesRecorded);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 3 < span.Length; offset += 4)
            {
                var sample = BitConverter.ToSingle(span.Slice(offset, 4));
                if (!float.IsFinite(sample))
                    continue;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 1 < span.Length; offset += 2)
            {
                var sample = BitConverter.ToInt16(span.Slice(offset, 2)) / 32768d;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 24)
        {
            for (var offset = 0; offset + 2 < span.Length; offset += 3)
            {
                var raw = span[offset] | (span[offset + 1] << 8) | (span[offset + 2] << 16);
                if ((raw & 0x800000) != 0)
                    raw |= unchecked((int)0xFF000000);
                var sample = raw / 8388608d;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 3 < span.Length; offset += 4)
            {
                var sample = BitConverter.ToInt32(span.Slice(offset, 4)) / 2147483648d;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }

        var rms = sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount);
        var dbfs = 20 * Math.Log10(Math.Max(rms, 0.000001));
        Volatile.Write(ref _latestDbfs, Math.Clamp(dbfs, -60, 0));
        Interlocked.Increment(ref _callbackCount);
    }

    public void Dispose()
    {
        if (_capture is null)
            return;

        try
        {
            _capture.StopRecording();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Error ??= exception.Message;
        }

        _capture.Dispose();
        _capture = null;
    }
}
