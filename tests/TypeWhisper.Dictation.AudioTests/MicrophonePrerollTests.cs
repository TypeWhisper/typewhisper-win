using NAudio.Wave;
using Xunit;
using TypeWhisper.WinUI;
using TypeWhisper.WinUI.Platform;

public sealed class MicrophonePrerollTests
{
    [Fact]
    public void KeepsOnlyHalfASecondAndPublishesItOnlyAfterRecordingStarts()
    {
        var input = new Input();
        using var audio = Create(input);
        var delivered = new List<float>();
        audio.SamplesAvailable += (_, args) => delivered.AddRange(args.Samples);
        audio.MicrophonePrerollEnabled = true;
        input.Feed(Enumerable.Repeat((short)1000, 10000).ToArray());
        Assert.Empty(delivered);
        Assert.False(audio.IsRecording);
        audio.StartRecording(false);
        Assert.Equal(8000, delivered.Count);
        input.Feed([2000, 3000]);
        var recorded = audio.StopRecording()!;
        Assert.Equal(8002, recorded.Length);
        Assert.Equal(recorded, delivered);
        Assert.Equal(1, input.Starts);
        Assert.True(audio.HasSpeechEnergy);
        // The prior dictation is not available as a prefix for the next one.
        audio.StartRecording(false);
        input.Feed([4000]);
        Assert.Single(audio.StopRecording()!);
    }

    [Fact]
    public void IsOffByDefaultAndReleasesIdleCaptureWhenDisabled()
    {
        var input = new Input();
        using var audio = Create(input);
        audio.WarmUp();
        Assert.Equal(0, input.Starts);
        audio.MicrophonePrerollEnabled = true;
        input.Feed([1000, 2000]);
        audio.MicrophonePrerollEnabled = false;
        Assert.False(input.Running);
        audio.StartRecording(false);
        input.Feed([3000]);
        Assert.Single(audio.StopRecording()!);
    }

    [Fact]
    public void SuspensionReleasesTheMicrophoneAndErasesThePrefix()
    {
        var input = new Input();
        using var audio = Create(input);
        audio.MicrophonePrerollEnabled = true;
        input.Feed([1000, 2000]);
        audio.SuspendMicrophonePreroll(true);
        Assert.False(input.Running);
        Assert.Equal(input.Created, input.Disposed);
        audio.WarmUp();
        Assert.False(input.Running);
        audio.SuspendMicrophonePreroll(false);
        Assert.True(input.Running);
        audio.StartRecording(false);
        input.Feed([3000]);
        Assert.Single(audio.StopRecording()!);
    }

    [Fact]
    public void RemoteDesktopNeverArmsTheIdleMicrophone()
    {
        var input = new Input();
        using var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), input, Timeout.InfiniteTimeSpan)
            { ReleaseCaptureBetweenRecordings = () => true, NormalizationEnabled = false };
        audio.MicrophonePrerollEnabled = true;
        Assert.Equal(0, input.Created);
        audio.StartRecording(false);
        input.Feed([2000]);
        Assert.Single(audio.StopRecording()!);
        Assert.False(input.Running);
        Assert.Equal(input.Created, input.Disposed);
    }

    [Fact]
    public void SessionReasonsDoNotResumeEachOther()
    {
        var state = new MicrophonePrerollSuspension();
        Assert.True(state.Observe(0x218, 4, true)); // sleep
        Assert.True(state.Observe(0x2B1, 8, true)); // unlock cannot undo sleep
        Assert.True(state.Observe(0x218, 18, false)); // wake still locked
        Assert.False(state.Observe(0x2B1, 8, true));
        Assert.True(state.Observe(0x2B1, 4, true)); // remote disconnect
        Assert.True(state.Observe(0x218, 7, true));
        Assert.False(state.Observe(0x2B1, 1, true)); // console reconnect
        Assert.True(state.Observe(0x2B1, 7, true)); // lock arrives before the desktop switch
        Assert.True(state.Observe(0x218, 18, true)); // a power event does not unlock the session
        Assert.False(state.Observe(0x2B1, 8, true));
    }

    [Fact]
    public void BoundedPreviewCopiesOnlyTheTailAndLeavesTheFullRecordingIntact()
    {
        var input = new Input();
        using var audio = Create(input);
        Assert.True(audio.WarmUp());
        audio.StartRecording(enableRecovery: false);
        input.Feed(Enumerable.Repeat((short)1000, 1000).ToArray());
        input.Feed(Enumerable.Repeat((short)2000, 500).ToArray());

        var tail = audio.GetCurrentBuffer(500)!;

        Assert.Equal(500, tail.Length);
        Assert.All(tail, sample => Assert.Equal(2000f / 32768f, sample));
        Assert.Equal(1500, audio.GetCurrentBuffer()!.Length);
        tail[0] = -1; // the snapshot owns its buffer
        var recording = audio.StopRecording()!;
        Assert.Equal(1500, recording.Length);
        Assert.Equal(1000f / 32768f, recording[0]);
        Assert.Equal(2000f / 32768f, recording[1000]);
    }

    private static AudioRecordingService Create(Input input) =>
        new(new ImmediateAudioTests.ReplayDevice(), input, Timeout.InfiniteTimeSpan) { NormalizationEnabled = false };

    private sealed class Input : IAudioInputCaptureFactory
    {
        private Capture? _capture;
        internal int Created, Disposed, Starts;
        internal bool Running => _capture?.Running == true;
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds)
        {
            Created++;
            return _capture = new(this);
        }
        internal void Feed(short[] samples) => _capture!.Feed(samples);

        private sealed class Capture(Input owner) : IAudioInputCapture
        {
            internal bool Running;
            public bool CanRestartAfterStop => true;
            public WaveFormat WaveFormat => new(16000, 16, 1);
            public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
            public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
            public void Prepare() { }
            public void StartRecording() { owner.Starts++; Running = true; }
            public void StopRecording() { Running = false; RecordingStopped?.Invoke(this, new()); }
            public void Dispose() { Running = false; owner.Disposed++; }
            internal void Feed(short[] samples)
            {
                if (!Running) return;
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                DataAvailable?.Invoke(this, new(bytes, bytes.Length));
            }
        }
    }
}
