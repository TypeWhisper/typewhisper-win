using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

// NAudio raises one callback per WASAPI packet and drains every packet of a wake-up back to back,
// so the timeline must place such a burst as a whole rather than end each packet at its receipt.
public sealed class SystemAudioTimelineTests
{
    private const int Packet = 160; // 10 ms at 16 kHz

    [Fact]
    public void PacketsDrainedInOneWakeUpEndWhenTheFirstArrived()
    {
        var (service, capture, clock, published) = Start();
        clock.Burst(capture, seconds: 1.0, packets: 5);

        var samples = service.StopCapture(TimeSpan.FromSeconds(1.0));

        Assert.Equal(samples, published);

        Assert.Equal(16000, samples.Length);
        Assert.Equal(16000 - 5 * Packet, Array.FindIndex(samples, sample => sample > 0));
    }

    [Fact]
    public void SchedulingJitterBetweenWakeUpsStaysContiguous()
    {
        var (service, capture, clock, published) = Start();
        clock.Burst(capture, seconds: 1.0, packets: 5);
        clock.Burst(capture, seconds: 1.062, packets: 5);

        var samples = service.StopCapture(TimeSpan.FromSeconds(1.062));

        Assert.Equal(samples, published);

        var sound = samples.AsSpan(16000 - 5 * Packet, 10 * Packet).ToArray();
        Assert.All(sound, sample => Assert.NotEqual(0f, sample));
        Assert.Equal(16000 + 5 * Packet, Array.FindLastIndex(samples, sample => sample > 0) + 1);
    }

    [Fact]
    public void SilenceBetweenWakeUpsIsKept()
    {
        var (service, capture, clock, published) = Start();
        clock.Burst(capture, seconds: 1.0, packets: 5);
        clock.Burst(capture, seconds: 1.5, packets: 5);

        var samples = service.StopCapture(TimeSpan.FromSeconds(1.5));

        Assert.Equal(samples, published);

        Assert.Equal(24000, samples.Length);
        Assert.All(samples.AsSpan(16000, 24000 - 5 * Packet - 16000).ToArray(), sample => Assert.Equal(0f, sample));
        Assert.All(samples.AsSpan(24000 - 5 * Packet).ToArray(), sample => Assert.NotEqual(0f, sample));
    }

    // The published chunks must add up to the stopped capture, so streaming consumers see the
    // same timeline even though a burst moves earlier while its packets arrive.
    private static (SystemAudioCaptureService Service, FakeCapture Capture, FakeClock Clock, List<float> Published) Start()
    {
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var published = new List<float>();
        var service = new SystemAudioCaptureService(new FakeFactory(capture), () => clock.Now);
        service.SamplesAvailable += (_, e) => published.AddRange(e.Samples);
        service.StartCapture();
        return (service, capture, clock, published);
    }

    private sealed class FakeClock
    {
        internal TimeSpan Now { get; private set; }

        internal void Burst(FakeCapture capture, double seconds, int packets)
        {
            for (var packet = 0; packet < packets; packet++)
            {
                Now = TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(packet * 1000);
                capture.Raise(Packet);
            }
        }
    }

    private sealed class FakeFactory(FakeCapture capture) : ISystemAudioLoopbackCaptureFactory
    {
        public IReadOnlyList<SystemAudioOutputDevice> GetAvailableDevices() => [];
        public ISystemAudioLoopbackCapture Create(string? deviceId) => capture;
    }

    private sealed class FakeCapture : ISystemAudioLoopbackCapture
    {
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped { add { } remove { } }
        public WaveFormat WaveFormat { get; } = new(16000, 16, 1);
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { }

        internal void Raise(int samples)
        {
            var buffer = new byte[samples * sizeof(short)];
            for (var offset = 0; offset < buffer.Length; offset += sizeof(short))
                BitConverter.TryWriteBytes(buffer.AsSpan(offset), (short)16384);
            DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(buffer, buffer.Length));
        }
    }
}
