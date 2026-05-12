using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TypeWhisper.Plugin.LiveTranscript;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LiveTranscriptPluginTests
{
    [Fact]
    public void TranscriptionFailure_HidesProcessingWindow()
    {
        RunOnStaThread(() =>
        {
            var appCreated = Application.Current is null;
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var eventBus = new TestPluginEventBus();
            var host = new TestPluginHostServices(eventBus);
            var plugin = new LiveTranscriptPlugin();

            try
            {
                plugin.ActivateAsync(host).GetAwaiter().GetResult();

                eventBus.Publish(new RecordingStartedEvent());
                PumpDispatcher();

                var window = GetWindow(plugin);
                Assert.NotNull(window);
                Assert.True(window.IsVisible);
                Assert.Equal("Listening...", window.CurrentText);

                eventBus.Publish(new RecordingStoppedEvent());
                PumpDispatcher();

                Assert.True(window.IsVisible);
                Assert.Equal("Listening...\nProcessing...", window.CurrentText);

                eventBus.Publish(new TranscriptionFailedEvent { ErrorMessage = "No speech detected" });
                PumpDispatcher();

                Assert.False(window.IsVisible);
            }
            finally
            {
                plugin.DeactivateAsync().GetAwaiter().GetResult();
                PumpDispatcher();
                if (appCreated)
                    app.Shutdown();
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

    private sealed class TestPluginHostServices(IPluginEventBus eventBus) : IPluginHostServices
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
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
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
