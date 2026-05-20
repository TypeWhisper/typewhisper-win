using Moq;
using NAudio.Wave;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

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
    public void RecordingStopped_ResetsWarmupAndRaisesDeviceLost_WhenCaptureStopsUnexpectedly()
    {
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var sut = CreateService(devices, captures);
        Assert.True(sut.WarmUp());

        var lost = 0;
        sut.DeviceLost += (_, _) => lost++;
        captures.Created.Single().RaiseStopped();

        Assert.Equal(1, lost);
        Assert.False(sut.IsRecording);

        Assert.True(sut.WarmUp());
        Assert.Equal(2, captures.Created.Count);
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

    private static SettingsViewModel CreateSettingsViewModel(
        FakeSettingsService settings,
        AudioRecordingService audio,
        SpeechFeedbackService speech)
    {
        var api = new ApiServerController(Mock.Of<ILocalApiServer>(), settings);
        var cli = new CliInstallService();
        return new SettingsViewModel(settings, audio, api, cli, speech, dispatchToUi: action => action());
    }
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

    public IAudioInputCapture Create(int deviceNumber, WaveFormat waveFormat, int bufferMilliseconds)
    {
        var capture = new FakeAudioInputCapture(deviceNumber);
        Created.Add(capture);
        return capture;
    }
}

internal sealed class FakeAudioInputCapture(int deviceNumber) : IAudioInputCapture
{
    public int DeviceNumber { get; } = deviceNumber;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public void StartRecording() => Started = true;

    public void StopRecording() => Stopped = true;

    public void RaiseStopped() => RecordingStopped?.Invoke(this, new AudioInputRecordingStoppedEventArgs());

    public void Dispose() => Disposed = true;

    public void RaiseData(byte[] buffer, int bytesRecorded) =>
        DataAvailable?.Invoke(this, new AudioInputDataAvailableEventArgs(buffer, bytesRecorded));
}
