using Moq;
using NAudio.Wave;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Audio;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;
using System.Windows.Threading;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AudioRecordingServiceDeviceChangeTests
{
    [Fact]
    public void CheckForDeviceChanges_RaisesDevicesChanged_WhenDeviceNamesChangeWithSameCount()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.True(sut.WarmUp());

        var changes = 0;
        sut.DevicesChanged += (_, _) => changes++;

        devices.SetDevices("Built-in Microphone");
        sut.CheckForDeviceChanges();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void CheckForDeviceChanges_FallsBackWithoutClearingConfiguredSelection_WhenSelectedDeviceDisappears()
    {
        var devices = new FakeAudioInputDeviceProvider("Built-in Microphone", "USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        sut.SetMicrophoneDevice(1);
        Assert.True(sut.WarmUp());

        devices.SetDevices("Built-in Microphone");
        sut.CheckForDeviceChanges();

        Assert.Equal([1, 0], captures.Created.Select(c => c.DeviceNumber));
    }

    [Fact]
    public void CheckForDeviceChanges_ReactivatesRememberedDeviceName_WhenSelectedDeviceReconnectsAtDifferentIndex()
    {
        var devices = new FakeAudioInputDeviceProvider("Built-in Microphone", "USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        sut.SetMicrophoneDevice(1);
        Assert.True(sut.WarmUp());
        devices.SetDevices("Built-in Microphone");
        sut.CheckForDeviceChanges();

        var available = 0;
        sut.DeviceAvailable += (_, _) => available++;
        devices.SetDevices("USB Microphone", "Built-in Microphone");
        sut.CheckForDeviceChanges();

        Assert.Equal(1, available);
        Assert.Equal(0, captures.Created.Last().DeviceNumber);
    }

    [Fact]
    public void CheckForDeviceChanges_DoesNotRaiseDeviceAvailable_WhenDeviceAppearsWithoutReportedLoss()
    {
        var devices = new FakeAudioInputDeviceProvider();
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.False(sut.WarmUp());

        var available = 0;
        sut.DeviceAvailable += (_, _) => available++;
        devices.SetDevices("USB Microphone");
        sut.CheckForDeviceChanges();

        Assert.Equal(0, available);
        Assert.True(sut.WarmUp());
    }

    [Fact]
    public void RecordingStopped_WithoutException_DoesNotRaiseDeviceLost_WhenCaptureStopsCleanly()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.True(sut.WarmUp());

        var lost = 0;
        sut.DeviceLost += (_, _) => lost++;
        captures.Created.Single().RaiseStopped();

        Assert.Equal(0, lost);
        Assert.False(sut.IsRecording);

        Assert.True(sut.WarmUp());
        Assert.Equal(2, captures.Created.Count);
    }

    [Fact]
    public void RecordingStopped_WithException_DoesNotRaiseDeviceLost_WhenActiveDeviceStillAvailable()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.True(sut.WarmUp());

        var lost = 0;
        sut.DeviceLost += (_, _) => lost++;
        captures.Created.Single().RaiseStopped(new InvalidOperationException("Capture failed."));

        Assert.Equal(0, lost);
        Assert.False(sut.IsRecording);

        Assert.True(sut.WarmUp());
        Assert.Equal(2, captures.Created.Count);
    }

    [Fact]
    public void RecordingStopped_WithException_RaisesDeviceLost_WhenActiveDeviceIsUnavailable()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.True(sut.WarmUp());

        var lost = 0;
        sut.DeviceLost += (_, _) => lost++;
        devices.SetDevices();
        captures.Created.Single().RaiseStopped(new InvalidOperationException("Capture failed."));

        Assert.Equal(1, lost);
        Assert.False(sut.IsRecording);
    }

    [Fact]
    public void RecordingStopped_WithoutException_DoesNotRaiseDeviceLost_WhenActiveCaptureStopsCleanly()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.True(sut.WarmUp());
        sut.StartRecording();

        var lost = 0;
        sut.DeviceLost += (_, _) => lost++;
        captures.Created.Single().RaiseStopped();

        Assert.Equal(0, lost);
        Assert.False(sut.IsRecording);
    }

    [Fact]
    public void DataAvailable_ConvertsCaptureWaveFormatToTranscriptionSamples()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory
        {
            ActualWaveFormat = new WaveFormatExtensible(32000, 32, 2)
        };
        using var sut = CreateService(devices, captures);
        sut.NormalizationEnabled = false;
        Assert.True(sut.WarmUp());
        sut.StartRecording();

        var source = new float[]
        {
            0.2f, 0.6f,
            0.4f, 0.8f,
            0.6f, 1.0f,
            0.8f, 1.0f
        };
        var bytes = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);

        captures.Created.Single().RaiseData(bytes, bytes.Length);

        var samples = sut.StopRecording();

        Assert.NotNull(samples);
        Assert.Equal(2, samples.Length);
        Assert.Equal(0.4f, samples[0], precision: 3);
        Assert.Equal(0.8f, samples[1], precision: 3);
    }

    [Fact]
    public void DataAvailable_DoesNotPropagateAudioLevelSubscriberExceptions()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        sut.NormalizationEnabled = false;
        Assert.True(sut.WarmUp());
        sut.StartRecording();
        sut.AudioLevelChanged += (_, _) =>
            throw new InvalidOperationException("UI thread owns this object.");

        var source = new short[] { 8192, 16384, 8192, 16384 };
        var bytes = new byte[source.Length * sizeof(short)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);

        var exception = Record.Exception(() => captures.Created.Single().RaiseData(bytes, bytes.Length));
        var samples = sut.StopRecording();

        Assert.Null(exception);
        Assert.NotNull(samples);
        Assert.Equal(source.Length, samples.Length);
    }

    [Fact]
    public void WasapiDeviceOrdering_PreservesWaveInSelectionIndices_WhenWaveInNamesAreTruncated()
    {
        var wasapiDeviceNames = new[]
        {
            "Microphone (Creative Pebble Pro)",
            "Personal Mix (Elgato Virtual Audio)",
            "Microphone (HyperX QuadCast 2)",
            "Chat Mix (Elgato Virtual Audio)"
        };
        var waveInDeviceNames = new[]
        {
            "Microphone (HyperX QuadCast 2)",
            "Microphone (Creative Pebble Pro",
            "Personal Mix (Elgato Virtual Au",
            "Chat Mix (Elgato Virtual Audio)"
        };

        var order = WasapiAudioInputDeviceOrdering.BuildWaveInCompatibleOrder(
            wasapiDeviceNames,
            waveInDeviceNames);

        Assert.Equal([2, 0, 1, 3], order);
    }

    [Fact]
    public void CheckForDeviceChanges_ClearsRecordingState_WhenActiveDeviceDisappearsDuringRecording()
    {
        var devices = new FakeAudioInputDeviceProvider("Built-in Microphone", "USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        sut.SetMicrophoneDevice(1);
        Assert.True(sut.WarmUp());
        sut.StartRecording();

        devices.SetDevices("Built-in Microphone");
        sut.CheckForDeviceChanges();

        Assert.False(sut.IsRecording);
    }

    private static AudioRecordingService CreateService(
        FakeAudioInputDeviceProvider devices,
        FakeAudioInputCaptureFactory captures) =>
        new(devices, captures, Timeout.InfiniteTimeSpan);
}

public sealed class SettingsViewModelMicrophoneDeviceTests
{
    [Fact]
    public void Constructor_SelectsDefaultMicrophoneItem_WhenNoDeviceIsConfigured()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";

        var devices = new FakeAudioInputDeviceProvider();
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var settings = new FakeSettingsService(AppSettings.Default with { SelectedMicrophoneDevice = null });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        using var speech = new SpeechFeedbackService(settings, pluginManager, new FakeTtsProvider("windows-sapi", "System Voice"));

        var sut = CreateSettingsViewModel(settings, audio, speech);

        Assert.Null(sut.SelectedMicrophoneDevice);
        Assert.NotNull(sut.SelectedMicrophoneItem);
        Assert.Null(sut.SelectedMicrophoneItem!.DeviceNumber);
        Assert.Equal("Default", sut.SelectedMicrophoneItem.Name);
    }

    [Fact]
    public void DevicesChanged_RefreshesMicrophonesAndKeepsMissingSelectedDevice()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";

        var devices = new FakeAudioInputDeviceProvider("Built-in Microphone", "USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var settings = new FakeSettingsService(AppSettings.Default with { SelectedMicrophoneDevice = 1 });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        using var speech = new SpeechFeedbackService(settings, pluginManager, new FakeTtsProvider("windows-sapi", "System Voice"));
        var sut = CreateSettingsViewModel(settings, audio, speech);

        devices.SetDevices("Built-in Microphone");
        audio.CheckForDeviceChanges();

        Assert.Equal(1, sut.SelectedMicrophoneDevice);
        var placeholder = Assert.Single(sut.Microphones, m => m.DeviceNumber == 1);
        Assert.Contains("disconnected", placeholder.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(placeholder, sut.SelectedMicrophoneItem);
    }

    [Fact]
    public void SelectingDefaultMicrophoneItem_PersistsNullDeviceSelection()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";

        var devices = new FakeAudioInputDeviceProvider("Built-in Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var settings = new FakeSettingsService(AppSettings.Default with { SelectedMicrophoneDevice = 0 });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        using var speech = new SpeechFeedbackService(settings, pluginManager, new FakeTtsProvider("windows-sapi", "System Voice"));
        var sut = CreateSettingsViewModel(settings, audio, speech);

        sut.SelectedMicrophoneItem = Assert.Single(sut.Microphones, m => m.DeviceNumber is null);

        Assert.Null(sut.SelectedMicrophoneDevice);
        Assert.Null(settings.Current.SelectedMicrophoneDevice);
    }

    [Fact]
    public void SettingsRoutePreview_StartsForDictationAndStopsWhenLeaving()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";

        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var settings = new FakeSettingsService(AppSettings.Default with { SelectedMicrophoneDevice = 0 });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        using var speech = new SpeechFeedbackService(settings, pluginManager, new FakeTtsProvider("windows-sapi", "System Voice"));
        var settingsViewModel = CreateSettingsViewModel(settings, audio, speech);

        SettingsWindowViewModel.UpdateMicrophonePreviewForRoute(
            settingsViewModel,
            SettingsRoute.Dashboard,
            SettingsRoute.Dictation);

        Assert.True(audio.IsPreviewing);

        SettingsWindowViewModel.UpdateMicrophonePreviewForRoute(
            settingsViewModel,
            SettingsRoute.Dictation,
            SettingsRoute.Shortcuts);

        Assert.False(audio.IsPreviewing);
    }

    [Fact]
    public void SettingsDispatcher_DoesNotBlockCaptureThread_WhenUiThreadHasNotPumped()
    {
        RunOnStaThread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var dispatch = typeof(SettingsViewModel).GetMethod(
                "DispatchToUi",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(dispatch);

            using var returned = new ManualResetEventSlim();
            using var executed = new ManualResetEventSlim();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                dispatch.Invoke(null, [dispatcher, () => executed.Set()]);
                returned.Set();
            });

            Assert.True(returned.Wait(TimeSpan.FromSeconds(1)));
            Assert.False(executed.IsSet);

            PumpDispatcherUntil(() => executed.IsSet, TimeSpan.FromSeconds(1));

            Assert.True(executed.IsSet);
        });
    }

    private static SettingsViewModel CreateSettingsViewModel(
        FakeSettingsService settings,
        AudioRecordingService audio,
        SpeechFeedbackService speech)
    {
        var api = new ApiServerController(Mock.Of<ILocalApiServer>(), settings);
        var cli = new CliInstallService();
        return new SettingsViewModel(settings, audio, api, cli, speech, dispatchToUi: action => action());
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}

public sealed class RecorderAudioPipelineTests
{
    [Fact]
    public void SystemAudioConverter_ConvertsStereoFloat32ToSixteenKilohertzMono()
    {
        var source = new float[]
        {
            0.2f, 0.6f,
            0.4f, 0.8f,
            0.6f, 1.0f,
            0.8f, 1.0f
        };
        var bytes = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);

        var samples = SystemAudioCaptureService.ConvertToTranscriptionSamples(
            bytes,
            bytes.Length,
            WaveFormat.CreateIeeeFloatWaveFormat(32000, 2));

        Assert.Equal(2, samples.Length);
        Assert.Equal(0.4f, samples[0], precision: 3);
        Assert.Equal(0.8f, samples[1], precision: 3);
    }

    [Fact]
    public void SystemAudioConverter_ConvertsStereoInt16ToSixteenKilohertzMono()
    {
        var source = new short[]
        {
            8192, 16384,
            16384, 32767
        };
        var bytes = new byte[source.Length * sizeof(short)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);

        var samples = SystemAudioCaptureService.ConvertToTranscriptionSamples(
            bytes,
            bytes.Length,
            new WaveFormat(16000, 16, 2));

        Assert.Equal(2, samples.Length);
        Assert.Equal(0.375f, samples[0], precision: 3);
        Assert.Equal(0.75f, samples[1], precision: 3);
    }

    [Fact]
    public void RecorderTranscriptionBuffer_ReturnsMixedDeltasForEnabledSources()
    {
        var buffer = new RecorderTranscriptionBuffer(RecorderMicDuckingMode.Off);

        buffer.AppendMic([0.2f, 0.2f, 0.2f]);
        buffer.AppendSystem([0.6f, 0.6f]);

        Assert.Equal([0.4f, 0.4f, 0.2f], buffer.GetBufferDelta(0, micEnabled: true, systemEnabled: true), FloatComparer.Instance);
        Assert.Equal([0.2f], buffer.GetBufferDelta(2, micEnabled: true, systemEnabled: true), FloatComparer.Instance);
    }

    [Fact]
    public void RecorderCaptureService_PublishesStreamingDeltaUnderLock()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "RecorderCaptureService.cs");
        var method = TestFile.ExtractBlock(source, "private void PublishMixedDelta", 900);
        var lockIndex = method.IndexOf("lock (_lock)", StringComparison.Ordinal);
        var deltaIndex = method.IndexOf("GetBufferDelta(_lastPublishedSampleCount)", StringComparison.Ordinal);
        var updateIndex = method.IndexOf("_lastPublishedSampleCount += delta.Length", StringComparison.Ordinal);

        Assert.True(lockIndex >= 0, "PublishMixedDelta should lock around delta publishing state.");
        Assert.InRange(deltaIndex, lockIndex, updateIndex);
    }

    [Fact]
    public void SystemAudioLoopbackFactory_DisposesDeviceEnumerators()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "SystemAudioCaptureService.cs");
        var factory = TestFile.ExtractBlock(source, "internal sealed class WasapiLoopbackCaptureFactory", 1600);

        Assert.Equal(2, TestFile.CountOccurrences(factory, "using var enumerator = new MMDeviceEnumerator();"));
    }

    [Fact]
    public void RecorderCaptureService_NormalizesGeneratedRecordingFileName()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "RecorderCaptureService.cs");
        var method = TestFile.ExtractBlock(source, "private static string WriteOutputFile", 900);

        Assert.Contains("var safeFileName = Path.GetFileName(fileName);", method);
        Assert.Contains("string.IsNullOrEmpty(safeFileName)", method);
        Assert.Contains("Path.Join(", method);
        Assert.DoesNotContain("?? fileName", method);
    }

    [Fact]
    public void AudioCaptureDiagnostics_SanitizesPathSegmentsWithoutUnsafeFallback()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "AudioCaptureDiagnostics.cs");
        var method = TestFile.ExtractBlock(source, "private static string SafePathSegment", 400);

        Assert.Contains("Path.GetFileName(segment)", method);
        Assert.Contains("string.IsNullOrEmpty(fileName) ? string.Empty : fileName", method);
        Assert.DoesNotContain("?? segment", method);
    }

    [Fact]
    public void SystemAudioCapture_UsesSelectedOutputDevice()
    {
        var factory = new FakeSystemAudioLoopbackCaptureFactory(
            [new SystemAudioOutputDevice("wave-link-monitor", "Wave Link Monitor")]);
        using var sut = new SystemAudioCaptureService(factory);

        sut.StartCapture("wave-link-monitor");
        var samples = sut.StopCapture();

        Assert.Equal("wave-link-monitor", factory.LastDeviceId);
        Assert.Empty(samples);
    }

    [Fact]
    public void RecorderMixer_AggressiveDucking_ReducesMicWhenSystemAudioIsPresent()
    {
        var mic = Enumerable.Repeat(0.5f, 16).ToArray();
        var system = Enumerable.Repeat(0.5f, 16).ToArray();

        var off = RecorderMixer.Mix(mic, system, RecorderMicDuckingMode.Off);
        var ducked = RecorderMixer.Mix(mic, system, RecorderMicDuckingMode.Aggressive);

        Assert.True(ducked.Last() < off.Last());
    }

    [Fact]
    public void RecorderMixer_OutputMixPreservesSystemAudioLevel()
    {
        var output = RecorderMixer.MixForOutput(
            micSamples: [0.2f],
            systemSamples: [0.6f],
            RecorderMicDuckingMode.Off);

        Assert.Equal([0.8f], output, FloatComparer.Instance);
    }

    [Fact]
    public void RecorderMixer_InterleavesSeparateTracksAsMicLeftSystemRight()
    {
        var interleaved = RecorderMixer.InterleaveSeparateTracks(
            micSamples: [0.1f, 0.2f],
            systemSamples: [0.8f]);

        Assert.Equal([0.1f, 0.8f, 0.2f, 0f], interleaved, FloatComparer.Instance);
    }

    private sealed class FloatComparer : IEqualityComparer<float>
    {
        public static readonly FloatComparer Instance = new();

        public bool Equals(float x, float y) =>
            Math.Abs(x - y) < 0.0001f;

        public int GetHashCode(float obj) => obj.GetHashCode();
    }
}

internal sealed class FakeSystemAudioLoopbackCaptureFactory(IReadOnlyList<SystemAudioOutputDevice>? devices = null)
    : ISystemAudioLoopbackCaptureFactory
{
    public string? LastDeviceId { get; private set; }
    public int AvailableDeviceRequestCount { get; private set; }

    public IReadOnlyList<SystemAudioOutputDevice> GetAvailableDevices()
    {
        AvailableDeviceRequestCount++;
        return devices ?? [];
    }

    public ISystemAudioLoopbackCapture Create(string? deviceId)
    {
        LastDeviceId = deviceId;
        return new FakeSystemAudioLoopbackCapture();
    }
}

internal sealed class FakeSystemAudioLoopbackCapture : ISystemAudioLoopbackCapture
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public void StartRecording() { }

    public void StopRecording() =>
        RecordingStopped?.Invoke(this, new AudioInputRecordingStoppedEventArgs());

    public void Dispose() { }
}

internal sealed class FakeAudioInputDeviceProvider : IAudioInputDeviceProvider
{
    private readonly List<string> _deviceNames;

    public FakeAudioInputDeviceProvider(params string[] deviceNames)
    {
        _deviceNames = [.. deviceNames];
    }

    public int DeviceCount => _deviceNames.Count;

    public string GetDeviceName(int deviceNumber) => _deviceNames[deviceNumber];

    public void SetDevices(params string[] deviceNames)
    {
        _deviceNames.Clear();
        _deviceNames.AddRange(deviceNames);
    }
}

internal sealed class FakeAudioInputCaptureFactory : IAudioInputCaptureFactory
{
    public List<FakeAudioInputCapture> Created { get; } = [];
    public WaveFormat? ActualWaveFormat { get; set; }

    public IAudioInputCapture Create(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds)
    {
        var capture = new FakeAudioInputCapture(deviceNumber, ActualWaveFormat ?? waveFormat);
        Created.Add(capture);
        return capture;
    }
}

internal sealed class FakeAudioInputCapture(int deviceNumber, WaveFormat waveFormat) : IAudioInputCapture
{
    public int DeviceNumber { get; } = deviceNumber;
    public WaveFormat WaveFormat { get; } = waveFormat;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public void StartRecording() => Started = true;

    public void StopRecording() => Stopped = true;

    public void RaiseStopped(Exception? exception = null) =>
        RecordingStopped?.Invoke(this, new AudioInputRecordingStoppedEventArgs(exception));

    public void Dispose() => Disposed = true;

    public void RaiseData(byte[] buffer, int bytesRecorded) =>
        DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(buffer, bytesRecorded));
}
