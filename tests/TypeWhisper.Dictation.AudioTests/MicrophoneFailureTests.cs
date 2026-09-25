using System.Runtime.InteropServices;
using NAudio;
using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class MicrophoneFailureTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070005), "Privacy & security")]
    [InlineData(unchecked((int)0x8889000A), "exclusively")]
    [InlineData(unchecked((int)0x88890004), "no longer available")]
    [InlineData(unchecked((int)0x88890008), "recording format")]
    [InlineData(unchecked((int)0x88890010), "Windows Audio service")]
    public void WasapiErrorsNameTheirCause(int hresult, string expected) =>
        Assert.Contains(expected, MicrophoneFailure.Describe(new COMException("native", hresult)));

    [Fact]
    public void OtherFailuresKeepTheGenericHint()
    {
        Assert.Contains("exclusively", MicrophoneFailure.Describe(new MmException(MmResult.AlreadyAllocated, "waveInOpen")));
        Assert.Contains("Privacy & security", MicrophoneFailure.Describe(new InvalidOperationException("wrapped", new UnauthorizedAccessException())));
        Assert.Equal(MicrophoneFailure.Generic, MicrophoneFailure.Describe(new InvalidOperationException("unknown")));
    }

    [Fact]
    public void FailedFallbackReportsThePrimaryError()
    {
        var blocked = new COMException("Access denied", unchecked((int)0x80070005));
        var factory = new FallbackAudioInputCaptureFactory(new Failing(blocked), new Failing(new MmException(MmResult.BadDeviceId, "waveInOpen")));
        var error = Assert.Throws<COMException>(() => factory.Create(new("id", "Mic", 0), new WaveFormat(16000, 16, 1), 30));
        Assert.Same(blocked, error);
    }

    [Fact]
    public void ServiceExposesTheLastPrepareFailureUntilASuccess()
    {
        var factory = new Switchable { Error = new COMException("Access denied", unchecked((int)0x80070005)) };
        using var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), factory, Timeout.InfiniteTimeSpan);
        Assert.False(audio.WarmUp());
        Assert.Contains("Privacy & security", audio.CaptureFailure);
        factory.Error = null;
        Assert.True(audio.WarmUp());
        Assert.Null(audio.CaptureFailure);
    }

    [Fact]
    public void ChangingTheMicrophoneTargetClearsAnObsoleteFailure()
    {
        var factory = new Switchable { Error = new COMException("Access denied", unchecked((int)0x80070005)) };
        using var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), factory, Timeout.InfiniteTimeSpan);
        Assert.False(audio.WarmUp());
        Assert.NotNull(audio.CaptureFailure);
        audio.SetMicrophonePriorityList([new("unplugged", "Unplugged headset")]);
        Assert.Null(audio.CaptureFailure);
        // A retry that finds no listed microphone does not resurrect the old error either.
        Assert.False(audio.WarmUp());
        Assert.Null(audio.CaptureFailure);
    }

    [Fact]
    public void PriorityNoticeFollowsTheDeviceResolver()
    {
        AudioInputDeviceInfo[] devices = [new(0, "new-id", "USB Mic", false), new(1, "laptop", "Laptop Mic", true)];
        Assert.Null(MicrophoneFailure.PriorityNotice([], devices));
        // A changed endpoint ID still resolves by name, like FindPriorityDeviceNumber.
        Assert.Null(MicrophoneFailure.PriorityNotice([new("old-id", "USB Mic")], devices));
        Assert.Equal("Headset disconnected · using Laptop Mic",
            MicrophoneFailure.PriorityNotice([new("headset", "Headset"), new("laptop", "Laptop Mic")], devices));
        // Unlisted microphones are never used when the priority list has entries.
        Assert.Contains("Headset disconnected. Reconnect", MicrophoneFailure.PriorityNotice([new("headset", "Headset")], devices));
    }

    [Fact]
    public void DeferredWarmUpKeepsTheFailureOfTheLastOpenAttempt()
    {
        var factory = new Switchable { Error = new COMException("Access denied", unchecked((int)0x80070005)) };
        using var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), factory, Timeout.InfiniteTimeSpan)
            { ReleaseCaptureBetweenRecordings = () => true };
        audio.StartRecording(enableRecovery: false);
        Assert.False(audio.IsRecording);
        // A remote-session warm-up opens nothing, so it cannot vouch that the microphone works now.
        Assert.True(audio.WarmUp());
        Assert.Contains("Privacy & security", audio.CaptureFailure);
    }

    [Fact]
    public void PriorityEditKeepingTheTargetKeepsItsFailure()
    {
        var factory = new Switchable { Error = new COMException("Access denied", unchecked((int)0x80070005)) };
        using var audio = new AudioRecordingService(new ImmediateAudioTests.ReplayDevice(), factory, Timeout.InfiniteTimeSpan);
        Assert.False(audio.WarmUp());
        // Only a lower-priority fallback is added; the failing microphone is still the one used.
        audio.SetMicrophonePriorityList([new("replay", "Synthetic replay"), new("usb", "USB Mic")]);
        Assert.Contains("Privacy & security", audio.CaptureFailure);
    }

    [Fact]
    public void DeferredWarmUpOnAnotherMicrophoneForgetsTheFailure()
    {
        var devices = new Devices();
        var factory = new Switchable { Error = new COMException("In use", unchecked((int)0x8889000A)) };
        using var audio = new AudioRecordingService(devices, factory, Timeout.InfiniteTimeSpan)
            { ReleaseCaptureBetweenRecordings = () => true };
        audio.SetMicrophonePriorityList([new("usb", "USB Mic"), new("laptop", "Laptop Mic")]);
        audio.StartRecording(enableRecovery: false);
        Assert.Contains("exclusively", audio.CaptureFailure);
        // Unplugging the failed microphone makes the topology change target the fallback without opening it.
        devices.List = [new(0, "laptop", "Laptop Mic", true)];
        audio.CheckForDeviceChanges();
        Assert.Null(audio.CaptureFailure);
    }

    [Fact]
    public void DeviceChangeIsReportedAfterTheFallbackWasTried()
    {
        var devices = new Devices();
        var factory = new Switchable();
        using var audio = new AudioRecordingService(devices, factory, Timeout.InfiniteTimeSpan);
        audio.SetMicrophonePriorityList([new("usb", "USB Mic"), new("laptop", "Laptop Mic")]);
        Assert.True(audio.WarmUp());
        string? seen = "not raised";
        audio.DevicesChanged += (_, _) => seen = audio.CaptureFailure;
        devices.List = [new(0, "laptop", "Laptop Mic", true)];
        factory.Error = new COMException("In use", unchecked((int)0x8889000A));
        audio.CheckForDeviceChanges();
        Assert.Contains("exclusively", seen);
    }

    [Fact]
    public void FailingDeviceListenerDoesNotEscapeTheDeviceCheck()
    {
        var devices = new Devices();
        using var audio = new AudioRecordingService(devices, new Switchable(), Timeout.InfiniteTimeSpan);
        Assert.True(audio.WarmUp());
        audio.DevicesChanged += (_, _) => throw new InvalidOperationException("listener");
        devices.List = [new(1, "laptop", "Laptop Mic", true)];
        audio.CheckForDeviceChanges();
        Assert.Null(audio.CaptureFailure);
    }

    [Fact]
    public void FailedDefaultMicrophoneMigrationIsReported()
    {
        var devices = new Devices();
        var factory = new Switchable();
        using var audio = new AudioRecordingService(devices, factory, Timeout.InfiniteTimeSpan);
        Assert.True(audio.WarmUp());
        audio.CheckForDeviceChanges();
        string? seen = "not raised";
        audio.DevicesChanged += (_, _) => seen = audio.CaptureFailure;
        // Same endpoints, only the Windows default moves.
        devices.List = [new(0, "usb", "USB Mic", true), new(1, "laptop", "Laptop Mic", false)];
        factory.Error = new COMException("In use", unchecked((int)0x8889000A));
        audio.CheckForDeviceChanges();
        Assert.Contains("exclusively", seen);
    }

    [Fact]
    public void DefaultSwitchAfterAFailedPrepareRetriesTheNewDefault()
    {
        var devices = new Devices();
        var factory = new Switchable { Error = new COMException("In use", unchecked((int)0x8889000A)) };
        using var audio = new AudioRecordingService(devices, factory, Timeout.InfiniteTimeSpan);
        Assert.False(audio.WarmUp());
        audio.CheckForDeviceChanges();
        Assert.Equal(1, factory.Created);
        string? seen = "not raised";
        audio.DevicesChanged += (_, _) => seen = audio.CaptureFailure;
        // Same endpoints, only the Windows default moves away from the failed microphone.
        devices.List = [new(0, "usb", "USB Mic", true), new(1, "laptop", "Laptop Mic", false)];
        factory.Error = null;
        audio.CheckForDeviceChanges();
        Assert.Equal(2, factory.Created);
        Assert.Null(seen);
        Assert.Null(audio.CaptureFailure);
    }

    [Fact]
    public void UnchangedFailedDefaultIsNotReopenedOnEveryCheck()
    {
        var devices = new Devices();
        var factory = new Switchable { Error = new COMException("In use", unchecked((int)0x8889000A)) };
        using var audio = new AudioRecordingService(devices, factory, Timeout.InfiniteTimeSpan);
        Assert.False(audio.WarmUp());
        audio.CheckForDeviceChanges();
        audio.CheckForDeviceChanges();
        Assert.Equal(1, factory.Created);
        Assert.Contains("exclusively", audio.CaptureFailure);
    }

    [Fact]
    public void DefaultSwitchAfterARemoteStartFailureOnlyTracksTheNewDefault()
    {
        var devices = new Devices();
        var factory = new Switchable { Error = new COMException("In use", unchecked((int)0x8889000A)) };
        using var audio = new AudioRecordingService(devices, factory, Timeout.InfiniteTimeSpan)
            { ReleaseCaptureBetweenRecordings = () => true };
        audio.StartRecording(enableRecovery: false);
        Assert.Contains("exclusively", audio.CaptureFailure);
        string? seen = "not raised";
        audio.DevicesChanged += (_, _) => seen = audio.CaptureFailure;
        devices.List = [new(0, "usb", "USB Mic", true), new(1, "laptop", "Laptop Mic", false)];
        audio.CheckForDeviceChanges();
        // The failure described the old default; the new one is opened by the next recording.
        Assert.Equal(1, factory.Created);
        Assert.Null(seen);
        Assert.Null(audio.CaptureFailure);
    }

    [Fact]
    public void SameMicrophoneMatchesByIdOrName()
    {
        Assert.True(MicrophoneFailure.IsSameMicrophone(new(0, "new-id", "USB Mic", false), new("old-id", "USB Mic")));
        Assert.True(MicrophoneFailure.IsSameMicrophone(new(0, "usb", "Renamed", false), new("usb", "USB Mic")));
        Assert.False(MicrophoneFailure.IsSameMicrophone(new(0, "laptop", "Laptop Mic", false), new("usb", "USB Mic")));
    }

    [Fact]
    public void MicrophoneWithANewEndpointIdReplacesItsSavedEntry()
    {
        AudioInputDeviceInfo reinstalled = new(0, "new-id", "USB Mic", false);
        AudioInputDeviceInfo second = new(1, "usb-2", "USB Mic 2", false);
        AudioInputDeviceInfo[] devices = [reinstalled, second, new(2, "laptop", "Laptop Mic", true)];
        Assert.Equal(1, MicrophoneFailure.ReplacedEntryIndex([new("laptop", "Laptop Mic"), new("old-id", "USB Mic")], reinstalled, devices));
        // A prefix match is a different microphone, even though the resolver would accept it.
        Assert.Equal(-1, MicrophoneFailure.ReplacedEntryIndex([new("old-id", "USB Mic")], second, devices));
        // A connected saved entry is its own microphone, so an identically named one is new.
        Assert.Equal(-1, MicrophoneFailure.ReplacedEntryIndex([new("usb-2", "USB Mic")], reinstalled, devices));
        Assert.Equal(-1, MicrophoneFailure.ReplacedEntryIndex([], reinstalled, devices));
        // Two disconnected entries with the same name leave no safe choice, so the device is added separately.
        Assert.Equal(-1, MicrophoneFailure.ReplacedEntryIndex([new("twin-a", "USB Mic"), new("twin-b", "USB Mic")], reinstalled, devices));
    }

    private sealed class Devices : IAudioInputDeviceProvider
    {
        public AudioInputDeviceInfo[] List = [new(0, "usb", "USB Mic", false), new(1, "laptop", "Laptop Mic", true)];
        public int DeviceCount => List.Length;
        public string GetDeviceName(int index) => List[index].Name;
        public string? GetDefaultDeviceName() => List.FirstOrDefault(device => device.IsDefault)?.Name;
        public AudioInputDeviceInfo GetDeviceInfo(int index) => List[index];
        public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos() => List;
    }

    private sealed class Failing(Exception error) : IAudioInputCaptureFactory
    {
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds) => throw error;
    }

    private sealed class Switchable : IAudioInputCaptureFactory
    {
        public Exception? Error;
        public int Created;
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds)
        {
            Created++;
            return Error is { } error ? throw error : new ImmediateAudioTests.ReplayInput();
        }
    }
}
