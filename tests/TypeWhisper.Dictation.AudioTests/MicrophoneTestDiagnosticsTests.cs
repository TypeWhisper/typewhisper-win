using System.Runtime.InteropServices;
using NAudio.Wave;
using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class MicrophoneTestDiagnosticsTests
{
    [Fact]
    public void WindowsSilenceDoesNotReadTheNativePointerOrCountAsRealZeros()
    {
        byte[] buffer = [12, 34, 56, 78, 90, 12];
        var kind = AudioPacketDiagnostics.CopyPacket(IntPtr.Zero, buffer, 2, 2, true, new WaveFormat(16000, 16, 1));
        Assert.Equal(AudioPacketKind.WindowsSilent, kind);
        Assert.Equal(new byte[] { 12, 34, 0, 0, 90, 12 }, buffer);
    }

    [Fact]
    public void UnflaggedNativeZerosAndNonzeroDataRemainDistinct()
    {
        var pointer = Marshal.AllocHGlobal(4);
        try
        {
            var buffer = new byte[6];
            Marshal.Copy(new byte[4], 0, pointer, 4);
            Assert.Equal(AudioPacketKind.ZeroSamples,
                AudioPacketDiagnostics.CopyPacket(pointer, buffer, 1, 4, false, new WaveFormat(16000, 16, 1)));
            Marshal.Copy(new byte[] { 0, 0, 1, 0 }, 0, pointer, 4);
            Assert.Equal(AudioPacketKind.Signal,
                AudioPacketDiagnostics.CopyPacket(pointer, buffer, 1, 4, false, new WaveFormat(16000, 16, 1)));
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0 }, buffer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void SignedPcmChecksEveryChannelWithoutAnEnergyThreshold(int bits)
    {
        var format = new WaveFormat(48000, bits, 2);
        var buffer = new byte[format.BlockAlign * 2];
        Assert.Equal(AudioPacketKind.ZeroSamples, AudioPacketDiagnostics.Classify(buffer, format));
        buffer[^1] = 1;
        Assert.Equal(AudioPacketKind.Signal, AudioPacketDiagnostics.Classify(buffer, format));
    }

    [Fact]
    public void UnsignedEightBitPcmUsesItsNumericZero()
    {
        var format = new WaveFormat(16000, 8, 1);
        Assert.Equal(AudioPacketKind.ZeroSamples, AudioPacketDiagnostics.Classify(new byte[] { 128, 128 }, format));
        Assert.Equal(AudioPacketKind.Signal, AudioPacketDiagnostics.Classify(new byte[] { 0, 0 }, format));
    }

    [Fact]
    public void FloatNegativeZeroIsZeroAndTinyNonzeroSamplesAreSignal()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        byte[] zeros = [.. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(-0f)];
        Assert.Equal(AudioPacketKind.ZeroSamples, AudioPacketDiagnostics.Classify(zeros, format));
        byte[] tiny = [.. BitConverter.GetBytes(float.Epsilon), .. BitConverter.GetBytes(0f)];
        Assert.Equal(AudioPacketKind.Signal, AudioPacketDiagnostics.Classify(tiny, format));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void ExtensiblePcmAndFloatFormatsRetainTheirNumericZeroSemantics(int bits)
    {
        var format = new WaveFormatExtensible(48000, bits, 2);
        var data = new byte[format.BlockAlign];
        Assert.Equal(AudioPacketKind.ZeroSamples, AudioPacketDiagnostics.Classify(data, format));
        data[0] = 1;
        Assert.Equal(AudioPacketKind.Signal, AudioPacketDiagnostics.Classify(data, format));
    }

    [Fact]
    public void BatchedPacketsKeepSeparateCountsAndTheirLastKind()
    {
        var clock = new TestClock();
        var diagnostics = new MicrophoneTestDiagnostics("USB mic", true, clock);
        var first = new AudioPacketCounts().Add(AudioPacketKind.Signal).Add(AudioPacketKind.WindowsSilent);
        diagnostics.Observe(first, 0.2f);
        diagnostics.Observe(new AudioPacketCounts().Add(AudioPacketKind.ZeroSamples), 0);
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(3, snapshot.Packets.Total);
        Assert.Equal(1, snapshot.Packets.WindowsSilent);
        Assert.Equal(1, snapshot.Packets.ZeroSamples);
        Assert.Equal(1, snapshot.Packets.Signal);
        Assert.Equal(MicrophoneTestState.ZeroSamples, snapshot.State);
    }

    [Fact]
    public void MissingPacketsAreDetectedAfterStartupAndAfterSignalStops()
    {
        var clock = new TestClock();
        var diagnostics = new MicrophoneTestDiagnostics("USB mic", true, clock);
        Assert.Equal(MicrophoneTestState.WaitingForPackets, diagnostics.Snapshot().State);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(MicrophoneTestState.WaitingForPackets, diagnostics.Snapshot().State);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(MicrophoneTestState.NoPackets, diagnostics.Snapshot().State);
        Assert.Equal(0, diagnostics.Snapshot().Packets.Total);
        diagnostics.Observe(new AudioPacketCounts().Add(AudioPacketKind.Signal), 0.5f);
        Assert.Equal(MicrophoneTestState.Signal, diagnostics.Snapshot().State);
        clock.Advance(MicrophoneTestDiagnostics.PacketTimeout);
        var stalled = diagnostics.Snapshot();
        Assert.Equal(MicrophoneTestState.NoPackets, stalled.State);
        Assert.Equal(0, stalled.Peak);
        Assert.Equal(1, stalled.Packets.Signal);
        diagnostics.Observe(new AudioPacketCounts().Add(AudioPacketKind.WindowsSilent), 0);
        Assert.Equal(MicrophoneTestState.WindowsSilent, diagnostics.Snapshot().State);
    }

    [Fact]
    public void StoppedResultsDoNotAgeOrAcceptLatePackets()
    {
        var clock = new TestClock();
        var diagnostics = new MicrophoneTestDiagnostics("USB mic", true, clock);
        diagnostics.Observe(new AudioPacketCounts().Add(AudioPacketKind.ZeroSamples), 0);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        diagnostics.Stop();
        var stopped = diagnostics.Snapshot();
        clock.Advance(TimeSpan.FromSeconds(30));
        diagnostics.Observe(new AudioPacketCounts().Add(AudioPacketKind.Signal), 1);
        Assert.False(stopped.Running);
        Assert.Equal(stopped, diagnostics.Snapshot());
    }

    [Fact]
    public void PreviewFollowsPriorityCapturesImmediatePacketsAndDoesNotRecord()
    {
        var input = new TestInput();
        using var audio = new AudioRecordingService(new Devices(), input, Timeout.InfiniteTimeSpan);
        audio.SetMicrophonePriorityList([new("usb", "USB mic")]);
        audio.StartPreview(null);
        Assert.Equal("usb", input.Selection?.Id);
        Assert.True(audio.IsPreviewing);
        Assert.False(audio.IsRecording);
        Assert.True(audio.MicrophoneTest?.HasWindowsFlags);
        Assert.Equal(MicrophoneTestState.Signal, audio.MicrophoneTest?.State);
        Assert.Equal(1, audio.MicrophoneTest?.Packets.Signal);
        audio.StopPreview();
        Assert.True(input.Disposed);
        Assert.False(audio.MicrophoneTest?.Running);
        audio.StartPreview(null);
        Assert.Equal(1, audio.MicrophoneTest?.Packets.Total);
    }

    [Fact]
    public void StereoCancellationDoesNotTurnNonzeroPacketsIntoZeroPackets()
    {
        var input = new TestInput { Format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 2), ImmediateSignal = false };
        using var audio = new AudioRecordingService(new Devices(), input, Timeout.InfiniteTimeSpan);
        audio.StartPreview(null);
        byte[] data = [.. BitConverter.GetBytes(0.25f), .. BitConverter.GetBytes(-0.25f)];
        input.Emit(data, new AudioPacketCounts().Add(AudioPacketKind.Signal));
        Assert.Equal(MicrophoneTestState.Signal, audio.MicrophoneTest?.State);
        Assert.Equal(0, audio.MicrophoneTest?.Packets.ZeroSamples);
    }

    [Fact]
    public void EmptyCallbacksAreNotAudioPackets()
    {
        var input = new TestInput { ImmediateSignal = false };
        using var audio = new AudioRecordingService(new Devices(), input, Timeout.InfiniteTimeSpan);
        audio.StartPreview(null);
        input.Emit([], null);
        Assert.Equal(0, audio.MicrophoneTest?.Packets.Total);
        Assert.Equal(MicrophoneTestState.WaitingForPackets, audio.MicrophoneTest?.State);
    }

    [Fact]
    public void FallbackCaptureDoesNotClaimToExposeWindowsFlags()
    {
        var input = new TestInput { HasWindowsPacketFlags = false, ImmediateSignal = false };
        using var audio = new AudioRecordingService(new Devices(), input, Timeout.InfiniteTimeSpan);
        audio.StartPreview(null);
        input.Emit(new byte[320], null);
        Assert.False(audio.MicrophoneTest?.HasWindowsFlags);
        Assert.Equal(MicrophoneTestState.ZeroSamples, audio.MicrophoneTest?.State);
    }

    [Fact]
    public void DisconnectedPriorityDoesNotSilentlyTestTheWindowsDefault()
    {
        var input = new TestInput();
        using var audio = new AudioRecordingService(new Devices(), input, Timeout.InfiniteTimeSpan);
        audio.SetMicrophonePriorityList([new("missing", "Headset")]);
        audio.StartPreview(null);
        Assert.Null(input.Selection);
        Assert.False(audio.IsPreviewing);
        Assert.Equal(MicrophoneTestState.Failed, audio.MicrophoneTest?.State);
        Assert.Contains("preferred microphone", audio.MicrophoneTest?.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartAndRuntimeFailuresAreReportedAndReleased(bool failAtStart)
    {
        var error = new UnauthorizedAccessException();
        var input = new TestInput { StartError = failAtStart ? error : null };
        using var audio = new AudioRecordingService(new Devices(), input, Timeout.InfiniteTimeSpan);
        audio.StartPreview(null);
        if (!failAtStart) input.Fail(error);
        Assert.Equal(MicrophoneTestState.Failed, audio.MicrophoneTest?.State);
        Assert.Contains("blocking microphone access", audio.MicrophoneTest?.Error);
        Assert.False(audio.IsPreviewing);
        audio.StopPreview();
        Assert.True(input.Disposed);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class Devices : IAudioInputDeviceProvider
    {
        public int DeviceCount => 2;
        public string GetDeviceName(int index) => index == 0 ? "USB mic" : "Laptop mic";
        public string? GetDefaultDeviceName() => "Laptop mic";
        public IReadOnlyList<AudioInputDeviceInfo> GetDeviceInfos() => [new(0, "usb", "USB mic", false), new(1, "laptop", "Laptop mic", true)];
        public AudioInputDeviceInfo GetDeviceInfo(int index) => GetDeviceInfos()[index];
    }

    private sealed class TestInput : IAudioInputCaptureFactory, IAudioInputCapture
    {
        internal AudioInputDeviceSelection? Selection;
        internal bool Disposed;
        internal bool ImmediateSignal = true;
        internal WaveFormat Format = new(16000, 16, 1);
        internal Exception? StartError;
        public bool CanRestartAfterStop => true;
        public bool HasWindowsPacketFlags { get; init; } = true;
        public WaveFormat WaveFormat => Format;
        public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
        public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;
        public IAudioInputCapture Create(AudioInputDeviceSelection selection, WaveFormat format, int bufferMilliseconds)
        { Selection = selection; Disposed = false; return this; }
        public void Prepare() { }
        public void StartRecording()
        {
            if (StartError is not null) throw StartError;
            if (ImmediateSignal) Emit([1, 0, 2, 0], new AudioPacketCounts().Add(AudioPacketKind.Signal));
        }
        public void StopRecording() => RecordingStopped?.Invoke(this, new());
        public void Dispose() => Disposed = true;
        internal void Emit(byte[] data, AudioPacketCounts? counts) => DataAvailable?.Invoke(this, new(data, data.Length, counts));
        internal void Fail(Exception error) => RecordingStopped?.Invoke(this, new(error));
    }
}
