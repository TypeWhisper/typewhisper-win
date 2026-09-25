using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class DeviceChangeRaceTests
{
    private static readonly AudioInputDeviceInfo QuadCast = new(0, "quadcast", "Microphone (QuadCast)", true);
    private static readonly AudioInputDeviceInfo Headset = new(1, "headset", "Microphone (Headset)", false);

    [Fact]
    public void DeviceCheckDoesNotStopARecordingThatMovedToTheFallbackMeanwhile()
    {
        var devices = new Devices { List = [QuadCast, Headset] };
        var captures = new Captures();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        audio.SetMicrophonePriorityList([new(QuadCast.Id, QuadCast.Name), new(Headset.Id, Headset.Name)]);
        Assert.True(audio.WarmUp());
        var lost = false;
        audio.DeviceLost += (_, _) => lost = true;

        // The QuadCast is switched off. Starting a recording finds its prepared capture stale and
        // releases it while a device notification is checked on another thread.
        devices.List = [Headset with { DeviceNumber = 0 }];
        Task? check = null;
        captures.Created[0].Capture.OnStop = () =>
        {
            var enumerated = new ManualResetEventSlim();
            devices.OnEnumerate = enumerated.Set;
            check = Task.Run(audio.CheckForDeviceChanges);
            Assert.True(enumerated.Wait(TimeSpan.FromSeconds(5)));
            // Give the check time to reach its decision while the recording start holds the capture.
            Thread.Sleep(200);
        };

        audio.StartRecording(enableRecovery: false);
        Assert.True(check!.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(Headset.Id, captures.Created[^1].DeviceId);
        Assert.True(audio.IsRecording);
        Assert.True(captures.Created[^1].Capture.Running);
        Assert.False(lost);
    }

    [Fact]
    public void ReconnectingPreferredMicrophoneDoesNotStopARecordingOnTheFallback()
    {
        var devices = new Devices { List = [Headset with { DeviceNumber = 0 }] };
        var captures = new Captures();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        audio.SetMicrophonePriorityList([new(QuadCast.Id, QuadCast.Name), new(Headset.Id, Headset.Name)]);
        audio.StartRecording(enableRecovery: false);
        Assert.True(audio.IsRecording);
        var lost = false;
        audio.DeviceLost += (_, _) => lost = true;

        // The QuadCast returns in front of the headset, which moves from index 0 to 1.
        devices.List = [QuadCast, Headset];
        audio.CheckForDeviceChanges();

        Assert.True(audio.IsRecording);
        Assert.False(lost);
        Assert.Equal(Headset.Id, captures.Created[^1].DeviceId);
        audio.StopRecording();

        // The deferred move to the preferred microphone happens with the next recording.
        audio.StartRecording(enableRecovery: false);
        Assert.Equal(QuadCast.Id, captures.Created[^1].DeviceId);
    }

    private sealed class Devices : IAudioInputDeviceProvider
    {
        public volatile AudioInputDeviceInfo[] List = [];
        public Action? OnEnumerate;
        public int DeviceCount => List.Length;
        public string GetDeviceName(int index) => List[index].Name;
        public string? GetDefaultDeviceName() => List.FirstOrDefault(device => device.IsDefault)?.Name;
        public AudioInputDeviceInfo GetDeviceInfo(int index) => List[index];
        public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos()
        {
            Interlocked.Exchange(ref OnEnumerate, null)?.Invoke();
            return List;
        }
    }

    private sealed class Captures : IAudioInputCaptureFactory
    {
        public readonly List<(string DeviceId, Input Capture)> Created = [];

        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds)
        {
            var capture = new Input();
            Created.Add((device.Id, capture));
            return capture;
        }
    }

    private sealed class Input : IAudioInputCapture
    {
        public Action? OnStop;
        public volatile bool Running;
        public bool CanRestartAfterStop => true;
        public WaveFormat WaveFormat => new(16000, 16, 1);
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
        public void Prepare() { }
        public void StartRecording() => Running = true;

        public void StopRecording()
        {
            Interlocked.Exchange(ref OnStop, null)?.Invoke();
            Running = false;
            RecordingStopped?.Invoke(this, new());
        }

        public void Dispose() => Running = false;
    }
}
