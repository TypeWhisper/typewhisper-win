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

    private sealed class Failing(Exception error) : IAudioInputCaptureFactory
    {
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds) => throw error;
    }

    private sealed class Switchable : IAudioInputCaptureFactory
    {
        public Exception? Error;
        public IAudioInputCapture Create(AudioInputDeviceSelection device, WaveFormat format, int bufferMilliseconds) =>
            Error is { } error ? throw error : new ImmediateAudioTests.ReplayInput();
    }
}
