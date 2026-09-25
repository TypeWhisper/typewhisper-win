using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class RemoteSessionCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteSessionsCloseTheCaptureBetweenRecordings(bool remote)
    {
        var input = new CountingInput();
        using (var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), input, Timeout.InfiniteTimeSpan)
            { NormalizationEnabled = false, ReleaseCaptureBetweenRecordings = () => remote })
        {
            Assert.True(audio.WarmUp());
            // Locally the prepared client stays open for immediate capture; remotely nothing is opened while idle.
            Assert.Equal(remote ? 0 : 1, input.Open);
            for (var i = 0; i < 2; i++)
            {
                audio.StartRecording(enableRecovery: false);
                Assert.Equal(1, input.Open);
                input.Feed(Enumerable.Repeat((short)3000, 480).ToArray());
                Assert.Equal(480, audio.StopRecording()?.Length);
                Assert.Equal(remote ? 0 : 1, input.Open);
            }
            Assert.Equal(remote ? 2 : 1, input.Created);
        }
        Assert.Equal(0, input.Open);
    }

    private sealed class CountingInput : IAudioInputCaptureFactory
    {
        public int Created, Disposed;
        public int Open => Created - Disposed;
        private Capture? _current;
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds)
        {
            Created++;
            return _current = new Capture(this);
        }
        public void Feed(short[] samples) => _current?.Feed(samples);

        private sealed class Capture(CountingInput owner) : IAudioInputCapture
        {
            private bool _running;
            public bool CanRestartAfterStop => true;
            public WaveFormat WaveFormat => new(16000, 16, 1);
            public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
            public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
            public void Prepare() { }
            public void StartRecording() => _running = true;
            public void StopRecording() { _running = false; RecordingStopped?.Invoke(this, new()); }
            public void Dispose() { _running = false; owner.Disposed++; }
            public void Feed(short[] samples)
            {
                if (!_running) return;
                var data = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, data, 0, data.Length);
                DataAvailable?.Invoke(this, new(data, data.Length));
            }
        }
    }
}
