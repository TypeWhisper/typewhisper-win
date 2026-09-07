using NAudio.Wave;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SystemAudioCleanupTests
{
    [Fact]
    public void LoopbackPreservesInitialSilenceGapsTailAndMixerPosition()
    {
        var now = TimeSpan.Zero;
        var native = new Capture();
        using var service = new SystemAudioCaptureService(new Factory(native), () => now);
        service.StartCapture();
        now = TimeSpan.FromSeconds(9);
        native.Emit(16000, 8192);
        now = TimeSpan.FromSeconds(12);
        native.Emit(16000, 16384);
        now = TimeSpan.FromSeconds(14);
        var audio = service.StopCapture();
        Assert.Equal(14 * 16000, audio.Length);
        Assert.All(audio[..(8 * 16000)], value => Assert.Equal(0f, value));
        Assert.Equal(0.25f, audio[8 * 16000]);
        Assert.All(audio[(9 * 16000)..(11 * 16000)], value => Assert.Equal(0f, value));
        Assert.Equal(0.5f, audio[11 * 16000]);
        Assert.All(audio[(12 * 16000)..], value => Assert.Equal(0f, value));
        var microphone = new float[audio.Length];
        microphone[8 * 16000] = 0.125f;
        var mixed = RecorderMixer.MixForOutput(microphone, audio, RecorderMicDuckingMode.Off);
        Assert.Equal(0f, mixed[0]);
        Assert.Equal(0.375f, mixed[8 * 16000]);
        Assert.Equal(audio.Length, mixed.Length);
    }

    [Fact]
    public void SilentCaptureUsesSharedStartOffsetAndFrozenStopDespiteCleanupRetry()
    {
        var now = TimeSpan.Zero;
        var native = new Capture { FailDispose = true };
        using var service = new SystemAudioCaptureService(new Factory(native), () => now);
        service.StartCapture(timelineOffset: TimeSpan.FromSeconds(2));
        now = TimeSpan.FromSeconds(3);
        Assert.Throws<InvalidOperationException>(() => service.StopCapture());
        now = TimeSpan.FromSeconds(30);
        native.FailDispose = false;
        var audio = service.StopCapture();
        Assert.Equal(5 * 16000, audio.Length);
        Assert.All(audio, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void ShortCallbackJitterDoesNotInsertSilenceBetweenContinuousPackets()
    {
        var now = TimeSpan.Zero;
        var native = new Capture();
        using var service = new SystemAudioCaptureService(new Factory(native), () => now);
        service.StartCapture();
        now = TimeSpan.FromSeconds(1);
        native.Emit(16000, 8192);
        now = TimeSpan.FromMilliseconds(2020);
        native.Emit(16000, 8192);
        var audio = service.StopCapture(timelineEnd: TimeSpan.FromSeconds(2));
        Assert.Equal(32000, audio.Length);
        Assert.All(audio, value => Assert.Equal(0.25f, value));
    }

    [Fact]
    public void StopFailureStillDisposesRealServiceCapture()
    {
        var native = new Capture { FailStop = true };
        using var service = new SystemAudioCaptureService(new Factory(native));
        service.StartCapture();
        service.StopCapture();
        Assert.True(native.Disposed);
        Assert.False(service.IsRecording);
        Assert.Equal(1, native.Stops);
    }

    [Fact]
    public void DisposeFailureKeepsCaptureOwnedUntilRetry()
    {
        var native = new Capture { FailStop = true, FailDispose = true };
        using var service = new SystemAudioCaptureService(new Factory(native));
        service.StartCapture();
        Assert.Throws<InvalidOperationException>(() => service.StopCapture());
        Assert.True(service.IsRecording);
        Assert.False(native.Disposed);
        native.FailDispose = false;
        service.StopCapture();
        Assert.False(service.IsRecording);
        Assert.True(native.Disposed);
    }

    private sealed class Factory(Capture capture) : ISystemAudioLoopbackCaptureFactory
    {
        public IReadOnlyList<SystemAudioOutputDevice> GetAvailableDevices() => [];
        public ISystemAudioLoopbackCapture Create(string? deviceId) => capture;
    }
    private sealed class Capture : ISystemAudioLoopbackCapture
    {
        public bool FailStop, FailDispose, Disposed;
        public int Stops;
        public WaveFormat WaveFormat => new(16000, 16, 1);
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped { add { } remove { } }
        public void StartRecording() { }
        public void StopRecording() { Stops++; if (FailStop) throw new InvalidOperationException("stop failed"); }
        public void Dispose() { if (FailDispose) throw new InvalidOperationException("dispose failed"); Disposed = true; }
        public void Emit(int count, short value)
        {
            var samples = Enumerable.Repeat(value, count).ToArray();
            var bytes = new byte[count * sizeof(short)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(bytes, bytes.Length));
        }
    }
}
