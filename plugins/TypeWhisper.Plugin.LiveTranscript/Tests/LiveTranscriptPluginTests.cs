using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TypeWhisper.Plugin.LiveTranscript;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.LiveTranscript.Tests;

public sealed class LiveTranscriptPluginTests
{
    private static LiveTranscriptPlugin CreatePlugin() => new();

    private static async Task<LiveTranscriptPlugin> ActivatedPlugin(
        TestPluginHostServices? host = null)
    {
        var sut = CreatePlugin();
        await sut.ActivateAsync(host ?? new TestPluginHostServices());
        return sut;
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.LiveTranscript", "manifest.json"));
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = CreatePlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void PluginId_IsExpectedValue()
    {
        var sut = CreatePlugin();
        Assert.Equal("com.typewhisper.live-transcript", sut.PluginId);
    }

    [Fact]
    public void PluginName_IsExpectedValue()
    {
        var sut = CreatePlugin();
        Assert.Equal("Live Transcript", sut.PluginName);
    }

    [Fact]
    public void FontSize_DefaultsTo16_WhenNoSettingStored()
    {
        var sut = CreatePlugin();
        Assert.Equal(16d, sut.FontSize);
    }

    [Fact]
    public void Opacity_DefaultsTo085_WhenNoSettingStored()
    {
        var sut = CreatePlugin();
        Assert.Equal(0.85, sut.Opacity);
    }

    [Fact]
    public void AutoHideMilliseconds_DefaultsTo3000_WhenNoSettingStored()
    {
        var sut = CreatePlugin();
        Assert.Equal(3000, sut.AutoHideMilliseconds);
    }

    [Fact]
    public async Task FontSize_PersistsToHostSettings()
    {
        var host = new TestPluginHostServices();
        var sut = await ActivatedPlugin(host);

        sut.FontSize = 20;

        Assert.Equal(20d, host.GetSetting<double?>("fontSize"));
    }

    [Fact]
    public async Task Opacity_PersistsToHostSettings()
    {
        var host = new TestPluginHostServices();
        var sut = await ActivatedPlugin(host);

        sut.Opacity = 0.5;

        Assert.Equal(0.5, host.GetSetting<double?>("opacity")!.Value, precision: 10);
    }

    [Fact]
    public async Task AutoHideMilliseconds_PersistsToHostSettings()
    {
        var host = new TestPluginHostServices();
        var sut = await ActivatedPlugin(host);

        sut.AutoHideMilliseconds = 1250;

        Assert.Equal(1250, host.GetSetting<int?>("autoHideMilliseconds"));
    }

    [Fact]
    public async Task WindowPosition_PersistsToHostSettings()
    {
        var host = new TestPluginHostServices();
        var sut = await ActivatedPlugin(host);

        sut.SaveWindowPosition(123.5, 456.25);

        Assert.Equal(123.5, host.GetSetting<double?>("windowLeft"));
        Assert.Equal(456.25, host.GetSetting<double?>("windowTop"));
    }

    [Fact]
    public async Task ResetWindowPosition_ClearsPersistedCoordinates()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("windowLeft", 123.5);
        host.SetSetting("windowTop", 456.25);
        var sut = await ActivatedPlugin(host);

        sut.ResetWindowPosition();

        Assert.Null(host.GetSetting<double?>("windowLeft"));
        Assert.Null(host.GetSetting<double?>("windowTop"));
    }

    [Fact]
    public async Task FontSize_ReadsPersistedValueFromHost()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("fontSize", 24d);
        var sut = await ActivatedPlugin(host);

        Assert.Equal(24d, sut.FontSize);
    }

    [Fact]
    public async Task FontSize_UsesHostAppearance_WhenAvailable()
    {
        var host = new AppearanceTestPluginHostServices
        {
            LiveTranscriptionFontSize = 10.5
        };
        host.SetSetting("fontSize", 24d);
        var sut = await ActivatedPlugin(host);

        Assert.Equal(10.5, sut.FontSize);
    }

    [Fact]
    public async Task AutoHideMilliseconds_UsesHostAppearance_WhenAvailable()
    {
        var host = new AppearanceTestPluginHostServices
        {
            PreviewBubbleAutoHideMilliseconds = 0
        };
        host.SetSetting("autoHideMilliseconds", 3000);
        var sut = await ActivatedPlugin(host);

        Assert.Equal(0, sut.AutoHideMilliseconds);
    }

    [Fact]
    public async Task Opacity_ReadsPersistedValueFromHost()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("opacity", 0.6);
        var sut = await ActivatedPlugin(host);

        Assert.Equal(0.6, sut.Opacity, precision: 10);
    }

    [Fact]
    public async Task AutoHideMilliseconds_ReadsPersistedValueFromHost()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("autoHideMilliseconds", 750);
        var sut = await ActivatedPlugin(host);

        Assert.Equal(750, sut.AutoHideMilliseconds);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotThrow()
    {
        var ex = await Record.ExceptionAsync(() => ActivatedPlugin());
        Assert.Null(ex);
    }

    [Fact]
    public async Task DeactivateAsync_AfterActivate_DoesNotThrow()
    {
        var sut = await ActivatedPlugin();
        var ex = await Record.ExceptionAsync(() => sut.DeactivateAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task DeactivateAsync_CalledTwice_DoesNotThrow()
    {
        var sut = await ActivatedPlugin();
        await sut.DeactivateAsync();
        var ex = await Record.ExceptionAsync(() => sut.DeactivateAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task ActivateAsync_SubscribesToSixEvents()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(6, host.Bus.SubscriptionCount);
    }

    [Fact]
    public async Task DeactivateAsync_DisposesAllSubscriptions()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);
        await sut.DeactivateAsync();

        Assert.Equal(0, host.Bus.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task TranscriptionFailedEvent_IsHandledWithoutException()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new TranscriptionFailedEvent { ErrorMessage = "No speech" }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordingStartedEvent_IsHandledWithoutException()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new RecordingStartedEvent()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordingStoppedEvent_IsHandledWithoutException()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new RecordingStoppedEvent()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task TranscriptionCompletedEvent_IsHandledWithoutException()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new TranscriptionCompletedEvent { Text = "Hello" }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task PartialTranscriptionUpdateEvent_IsHandledWithoutException()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new PartialTranscriptionUpdateEvent { PartialText = "Hel..." }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task TranscriptionFailed_AfterRecordingStarted_DoesNotThrow()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        await host.Bus.PublishAsync(new RecordingStartedEvent());
        await host.Bus.PublishAsync(new RecordingStoppedEvent());
        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new TranscriptionFailedEvent { ErrorMessage = "Timeout" }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task MultipleRecordingCycles_DoNotThrow()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        for (var i = 0; i < 3; i++)
        {
            await host.Bus.PublishAsync(new RecordingStartedEvent());
            await host.Bus.PublishAsync(new PartialTranscriptionUpdateEvent { PartialText = "..." });
            await host.Bus.PublishAsync(new RecordingStoppedEvent());
            await host.Bus.PublishAsync(new TranscriptionCompletedEvent { Text = $"Result {i}" });
        }
    }

    [Fact]
    public async Task FailedThenSuccessfulCycle_DoesNotThrow()
    {
        var host = new TestPluginHostServices();
        var sut = CreatePlugin();
        await sut.ActivateAsync(host);

        await host.Bus.PublishAsync(new RecordingStartedEvent());
        await host.Bus.PublishAsync(new RecordingStoppedEvent());
        await host.Bus.PublishAsync(new TranscriptionFailedEvent { ErrorMessage = "No speech" });

        await host.Bus.PublishAsync(new RecordingStartedEvent());
        await host.Bus.PublishAsync(new RecordingStoppedEvent());
        var ex = await Record.ExceptionAsync(() =>
            host.Bus.PublishAsync(new TranscriptionCompletedEvent { Text = "Hello" }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_AfterActivate_DoesNotThrow()
    {
        var sut = await ActivatedPlugin();
        var ex = Record.Exception(sut.Dispose);
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        var sut = await ActivatedPlugin();
        sut.Dispose();
        var ex = Record.Exception(sut.Dispose);
        Assert.Null(ex);
    }

    [Fact]
    public void TranscriptionFailure_HidesProcessingWindow()
    {
        RunOnStaThread(() =>
        {
            var eventBus = new TestPluginEventBus();
            var host = new WpfTestHostServices(eventBus);
            var workArea = SystemParameters.WorkArea;
            var expectedLeft = workArea.Left + 40;
            var expectedTop = workArea.Top + 40;
            host.SetSetting("windowLeft", expectedLeft);
            host.SetSetting("windowTop", expectedTop);
            host.SetSetting("autoHideMilliseconds", 0);
            var plugin = new LiveTranscriptPlugin();

            try
            {
                plugin.ActivateAsync(host).GetAwaiter().GetResult();

                var noSpeechRecordingId = Guid.NewGuid();
                eventBus.Publish(new TranscriptionFailedEvent
                {
                    RecordingId = noSpeechRecordingId,
                    ErrorMessage = "No speech detected"
                });
                PumpDispatcher();

                Assert.False(GetWindow(plugin)?.IsVisible == true);

                eventBus.Publish(new RecordingStartedEvent { RecordingId = noSpeechRecordingId });
                PumpDispatcher();

                Assert.False(GetWindow(plugin)?.IsVisible == true);

                eventBus.Publish(new RecordingStoppedEvent { RecordingId = noSpeechRecordingId });
                PumpDispatcher();

                Assert.False(GetWindow(plugin)?.IsVisible == true);

                eventBus.Publish(new RecordingStartedEvent());
                PumpDispatcher();

                var window = GetWindow(plugin);
                Assert.NotNull(window);
                Assert.True(window.IsVisible, "Window should be visible after a fresh recording starts.");
                Assert.Equal("Listening...", window.CurrentText);
                Assert.Equal(expectedLeft, window.Left, precision: 1);
                Assert.Equal(expectedTop, window.Top, precision: 1);

                eventBus.Publish(new RecordingStoppedEvent());
                PumpDispatcher();

                Assert.True(window.IsVisible, "Window should stay visible while the recording is processing.");
                Assert.Equal("Listening...\nProcessing...", window.CurrentText);

                eventBus.Publish(new TranscriptionFailedEvent { ErrorMessage = "No speech detected" });
                PumpDispatcher();

                Assert.False(window.IsVisible);

                var recordingIdWithoutFailureId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = recordingIdWithoutFailureId });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new RecordingStoppedEvent { RecordingId = recordingIdWithoutFailureId });
                eventBus.Publish(new TranscriptionFailedEvent { ErrorMessage = "No speech detected" });
                PumpDispatcher();

                Assert.False(window.IsVisible);

                var delayedNoIdFailureRecordingId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = delayedNoIdFailureRecordingId });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new RecordingStoppedEvent { RecordingId = delayedNoIdFailureRecordingId });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...\nProcessing...", window.CurrentText);

                var newRecordingId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = newRecordingId });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new TranscriptionFailedEvent { ErrorMessage = "No speech detected" });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new TranscriptionFailedEvent
                {
                    RecordingId = newRecordingId,
                    ErrorMessage = "No speech detected"
                });
                PumpDispatcher();

                Assert.False(window.IsVisible);

                var queuedStopRecordingId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = queuedStopRecordingId });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new RecordingStoppedEvent { RecordingId = queuedStopRecordingId });
                eventBus.Publish(new TranscriptionFailedEvent { ErrorMessage = "No speech detected" });
                PumpDispatcher();

                Assert.False(window.IsVisible);
                Assert.NotEqual("Listening...\nProcessing...", window.CurrentText);

                eventBus.Publish(new PartialTranscriptionUpdateEvent
                {
                    RecordingId = queuedStopRecordingId,
                    PartialText = "stale partial"
                });
                PumpDispatcher();

                Assert.False(window.IsVisible);
                Assert.NotEqual("stale partial", window.CurrentText);

                var queuedStopWithFailureId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = queuedStopWithFailureId });
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new RecordingStoppedEvent { RecordingId = queuedStopWithFailureId });
                eventBus.Publish(new TranscriptionFailedEvent
                {
                    RecordingId = queuedStopWithFailureId,
                    ErrorMessage = "No speech detected"
                });
                PumpDispatcher();

                Assert.False(window.IsVisible);
                Assert.NotEqual("Listening...\nProcessing...", window.CurrentText);

                host.SetSetting("windowLeft", 100000d);
                host.SetSetting("windowTop", 100000d);
                eventBus.Publish(new RecordingStartedEvent());
                PumpDispatcher();

                var windowWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                var windowHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
                Assert.InRange(window.Left, workArea.Left, workArea.Right - windowWidth + 1);
                Assert.InRange(window.Top, workArea.Top, workArea.Bottom - windowHeight + 1);

                eventBus.Publish(new TranscriptionCompletedEvent { Text = "Finished" });
                PumpDispatcher();
                Thread.Sleep(3300);
                PumpDispatcher();

                Assert.True(window.IsVisible, "Fallback auto-hide setting of 0 should keep the window visible after completion.");
                Assert.Equal("Finished", window.CurrentText);
            }
            finally
            {
                plugin.DeactivateAsync().GetAwaiter().GetResult();
                PumpDispatcher();
            }
        });
    }

    [Fact]
    public void TextInsertedEvent_StartsAppearanceAutoHideForMatchingRecording()
    {
        RunOnStaThread(() =>
        {
            var eventBus = new TestPluginEventBus();
            var host = new AppearanceWpfTestHostServices(eventBus)
            {
                LiveTranscriptionFontSize = 15,
                PreviewBubbleAutoHideMilliseconds = 25
            };
            host.SetSetting("autoHideMilliseconds", 0);
            var plugin = new LiveTranscriptPlugin();

            try
            {
                plugin.ActivateAsync(host).GetAwaiter().GetResult();

                var recordingId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = recordingId });
                PumpDispatcher();

                var window = GetWindow(plugin);
                Assert.NotNull(window);
                Assert.True(window.IsVisible, "Window should be visible after a fresh recording starts.");

                eventBus.Publish(new TranscriptionCompletedEvent
                {
                    RecordingId = recordingId,
                    Text = "Finished"
                });
                PumpDispatcher();
                Thread.Sleep(80);
                PumpDispatcher();

                Assert.True(window.IsVisible, "Completion alone should not start the Appearance auto-hide timer.");
                Assert.Equal("Finished", window.CurrentText);

                eventBus.Publish(new TextInsertedEvent
                {
                    RecordingId = Guid.NewGuid(),
                    Text = "Other"
                });
                PumpDispatcher();
                Thread.Sleep(80);
                PumpDispatcher();

                Assert.True(window.IsVisible, "A TextInsertedEvent for another recording must not hide the active window.");

                eventBus.Publish(new TextInsertedEvent
                {
                    RecordingId = recordingId,
                    Text = "Finished"
                });
                PumpDispatcher();
                Thread.Sleep(80);
                PumpDispatcher();

                Assert.False(window.IsVisible);
            }
            finally
            {
                plugin.DeactivateAsync().GetAwaiter().GetResult();
                PumpDispatcher();
            }
        });
    }

    [Fact]
    public void TextInsertedEvent_WithZeroAppearanceAutoHide_HidesImmediately()
    {
        RunOnStaThread(() =>
        {
            var eventBus = new TestPluginEventBus();
            var host = new AppearanceWpfTestHostServices(eventBus)
            {
                LiveTranscriptionFontSize = 15,
                PreviewBubbleAutoHideMilliseconds = 0
            };
            host.SetSetting("autoHideMilliseconds", 3000);
            var plugin = new LiveTranscriptPlugin();

            try
            {
                plugin.ActivateAsync(host).GetAwaiter().GetResult();

                var recordingId = Guid.NewGuid();
                eventBus.Publish(new RecordingStartedEvent { RecordingId = recordingId });
                PumpDispatcher();

                var window = GetWindow(plugin);
                Assert.NotNull(window);
                Assert.True(window.IsVisible, "Window should be visible after a fresh recording starts.");

                eventBus.Publish(new TranscriptionCompletedEvent
                {
                    RecordingId = recordingId,
                    Text = "Finished"
                });
                PumpDispatcher();
                Assert.True(window.IsVisible, "Completion alone should not hide when global auto-hide is 0.");

                eventBus.Publish(new TextInsertedEvent
                {
                    RecordingId = recordingId,
                    Text = "Finished"
                });
                PumpDispatcher();

                Assert.False(window.IsVisible);
            }
            finally
            {
                plugin.DeactivateAsync().GetAwaiter().GetResult();
                PumpDispatcher();
            }
        });
    }

    [Fact]
    public void SettingsView_HidesDuplicateAppearanceControls_WhenHostProvidesAppearance()
    {
        RunOnStaThread(() =>
        {
            var host = new AppearanceTestPluginHostServices
            {
                LiveTranscriptionFontSize = 14,
                PreviewBubbleAutoHideMilliseconds = 1500
            };
            var plugin = new LiveTranscriptPlugin();

            try
            {
                plugin.ActivateAsync(host).GetAwaiter().GetResult();

                var view = Assert.IsType<LiveTranscriptSettingsView>(plugin.CreateSettingsView());
                var fontSizePanel = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("FontSizePanel"));
                var autoHidePanel = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("AutoHidePanel"));

                Assert.Equal(Visibility.Collapsed, fontSizePanel.Visibility);
                Assert.Equal(Visibility.Collapsed, autoHidePanel.Visibility);
            }
            finally
            {
                plugin.DeactivateAsync().GetAwaiter().GetResult();
                PumpDispatcher();
            }
        });
    }

    private static LiveTranscriptWindow? GetWindow(LiveTranscriptPlugin plugin)
    {
        var field = typeof(LiveTranscriptPlugin).GetField(
            "_window",
            BindingFlags.Instance | BindingFlags.NonPublic);

        return (LiveTranscriptWindow?)field?.GetValue(plugin);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, JsonElement> _settings = [];

        public TrackingPluginEventBus Bus { get; } = new();
        public IPluginEventBus EventBus => Bus;

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new NoOpLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    }

    private sealed class AppearanceTestPluginHostServices : TestPluginHostServices, ILivePreviewAppearanceProvider
    {
        public double LiveTranscriptionFontSize { get; init; }
        public int PreviewBubbleAutoHideMilliseconds { get; init; }
    }

    private class WpfTestHostServices(IPluginEventBus eventBus) : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>()
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = eventBus;
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new NoOpLocalization();
    }

    private sealed class AppearanceWpfTestHostServices(IPluginEventBus eventBus)
        : WpfTestHostServices(eventBus), ILivePreviewAppearanceProvider
    {
        public double LiveTranscriptionFontSize { get; init; }
        public int PreviewBubbleAutoHideMilliseconds { get; init; }
    }

    private sealed class TrackingPluginEventBus : IPluginEventBus
    {
        private readonly List<TrackingSubscription> _subscriptions = [];

        public int SubscriptionCount => _subscriptions.Count;
        public int ActiveSubscriptionCount => _subscriptions.Count(s => !s.IsDisposed);

        public void Publish<T>(T pluginEvent) where T : PluginEvent
        {
            _ = PublishAsync(pluginEvent);
        }

        public async Task PublishAsync<T>(T pluginEvent) where T : PluginEvent
        {
            foreach (var sub in _subscriptions.Where(s => !s.IsDisposed && s.EventType == typeof(T)).ToList())
                await sub.InvokeAsync(pluginEvent);
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent
        {
            var sub = new TrackingSubscription(typeof(T), e => handler((T)e));
            _subscriptions.Add(sub);
            return sub;
        }
    }

    private sealed class TrackingSubscription(Type eventType, Func<PluginEvent, Task> handler) : IDisposable
    {
        public Type EventType { get; } = eventType;
        public bool IsDisposed { get; private set; }

        public Task InvokeAsync(PluginEvent evt) => handler(evt);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class NoOpLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        private readonly ConcurrentDictionary<Type, List<Func<PluginEvent, Task>>> _handlers = new();

        public void Publish<T>(T pluginEvent) where T : PluginEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var handlers))
                return;

            foreach (var handler in handlers.ToArray())
                handler(pluginEvent).GetAwaiter().GetResult();
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent
        {
            Func<PluginEvent, Task> wrapped = pluginEvent => handler((T)pluginEvent);
            var handlers = _handlers.GetOrAdd(typeof(T), _ => []);
            lock (handlers)
            {
                handlers.Add(wrapped);
            }

            return new Subscription(() =>
            {
                lock (handlers)
                {
                    handlers.Remove(wrapped);
                }
            });
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
