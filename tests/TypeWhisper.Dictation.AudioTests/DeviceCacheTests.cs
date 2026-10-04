using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class DeviceCacheTests
{
    private static readonly AudioInputDeviceInfo QuadCast = new(0, "quadcast", "Microphone (QuadCast)", true);
    private static readonly AudioInputDeviceInfo Headset = new(1, "headset", "Microphone (Headset)", false);

    [Fact]
    public void RecordingStartReusesTheEndpointListWhileNotificationsReportNoChange()
    {
        var devices = new Devices { List = [QuadCast, Headset] };
        var notifier = new Notifier();
        using var audio = new AudioRecordingService(devices, new Captures(), Timeout.InfiniteTimeSpan, notifier);
        Assert.True(audio.WarmUp());
        Assert.True(notifier.Started);

        devices.EnumerationsOn(Environment.CurrentManagedThreadId, out var before);
        audio.StartRecording(enableRecovery: false);
        devices.EnumerationsOn(Environment.CurrentManagedThreadId, out var after);

        Assert.True(audio.IsRecording);
        Assert.Equal(before, after);
    }

    [Fact]
    public void RecordingStartEnumeratesAgainAfterAnEndpointNotification()
    {
        var devices = new Devices { List = [QuadCast, Headset] };
        var notifier = new Notifier();
        var captures = new Captures();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan, notifier);
        audio.SetMicrophonePriorityList([new(QuadCast.Id, QuadCast.Name), new(Headset.Id, Headset.Name)]);
        Assert.True(audio.WarmUp());

        // The QuadCast is switched off. Whether the queued check or the recording start sees it
        // first, the recording must not start on the vanished microphone.
        devices.List = [Headset with { DeviceNumber = 0 }];
        notifier.Raise();
        audio.StartRecording(enableRecovery: false);

        Assert.True(audio.IsRecording);
        Assert.Equal(Headset.Id, captures.Created[^1].DeviceId);
    }

    [Fact]
    public void RecordingStartEnumeratesOnEveryStartWithoutNotifications()
    {
        var devices = new Devices { List = [QuadCast, Headset] };
        using var audio = new AudioRecordingService(devices, new Captures(), Timeout.InfiniteTimeSpan);
        Assert.True(audio.WarmUp());

        devices.EnumerationsOn(Environment.CurrentManagedThreadId, out var before);
        audio.StartRecording(enableRecovery: false);
        devices.EnumerationsOn(Environment.CurrentManagedThreadId, out var after);

        Assert.Equal(before + 1, after);
    }

    private sealed class Notifier : IAudioInputDeviceChangeNotifier
    {
        public bool Started;
        public event EventHandler? DevicesChanged;
        public bool Start() => Started = true;
        public void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }

    private sealed class Devices : IAudioInputDeviceProvider
    {
        private readonly Dictionary<int, int> _enumerationsByThread = [];
        public volatile AudioInputDeviceInfo[] List = [];
        public int DeviceCount => List.Length;
        public string GetDeviceName(int index) => List[index].Name;
        public string? GetDefaultDeviceName() => List.FirstOrDefault(device => device.IsDefault)?.Name;
        public AudioInputDeviceInfo GetDeviceInfo(int index) => List[index];

        public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos()
        {
            lock (_enumerationsByThread)
            {
                _enumerationsByThread.TryGetValue(Environment.CurrentManagedThreadId, out var count);
                _enumerationsByThread[Environment.CurrentManagedThreadId] = count + 1;
            }

            return List;
        }

        public void EnumerationsOn(int threadId, out int count)
        {
            lock (_enumerationsByThread)
                _enumerationsByThread.TryGetValue(threadId, out count);
        }
    }

    private sealed class Captures : IAudioInputCaptureFactory
    {
        public readonly List<(string DeviceId, Input Capture)> Created = [];

        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds)
        {
            var capture = new Input();
            lock (Created)
                Created.Add((device.Id, capture));
            return capture;
        }
    }

    private sealed class Input : IAudioInputCapture
    {
        public volatile bool Running;
        public bool CanRestartAfterStop => true;
        public WaveFormat WaveFormat => new(16000, 16, 1);
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
        public void Prepare() { }
        public void StartRecording() => Running = true;

        public void StopRecording()
        {
            Running = false;
            RecordingStopped?.Invoke(this, new());
        }

        public void Dispose() => Running = false;
    }
}
