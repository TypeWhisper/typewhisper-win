using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WelcomeViewModelTests
{
    [Fact]
    public void SelectedCloudModelWithoutApiKey_ExposesInlineSettingsAndBlocksActivation()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: false,
                supportsModelDownload: false);
            var sut = CreateViewModel(plugin);

            sut.SelectedModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "whisper-large-v3-turbo");

            Assert.True(sut.SelectedModelNeedsConfiguration);
            Assert.NotNull(sut.SelectedModelSettingsView);
            Assert.False(sut.CanApplySelectedModel);
            Assert.Equal("Groq", sut.SelectedModelConfigurationProviderName);
            Assert.Equal(1, plugin.CreateSettingsViewCallCount);
        });
    }

    [Fact]
    public void PluginConfigurationChange_ReenablesSelectedCloudModel()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: false,
                supportsModelDownload: false);
            var sut = CreateViewModel(plugin, out var pluginManager);
            sut.SelectedModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "whisper-large-v3-turbo");

            plugin.IsConfigured = true;
            TestPluginManagerFactory.InvokeRebuildCapabilityIndices(pluginManager);

            Assert.False(sut.SelectedModelNeedsConfiguration);
            Assert.Null(sut.SelectedModelSettingsView);
            Assert.True(sut.CanApplySelectedModel);
        });
    }

    [Fact]
    public void ModelList_OmitsSizeParentheses_WhenSizeDescriptionIsMissing()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: true,
                supportsModelDownload: false);
            var sut = CreateViewModel(plugin, out var pluginManager);

            TestPluginManagerFactory.InvokeRebuildCapabilityIndices(pluginManager);

            Assert.Contains(sut.AvailableModels, model => model.DisplayName == "Whisper Large V3 Turbo");
            Assert.DoesNotContain(sut.AvailableModels, model => model.DisplayName.Contains("()"));
        });
    }

    [Fact]
    public void FinalGetStarted_WithUnconfiguredCloudModel_RequestsPluginSettings()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: false,
                supportsModelDownload: false);
            var sut = CreateViewModel(plugin);
            sut.SelectedModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "whisper-large-v3-turbo");
            sut.CurrentStep = 3;
            var completed = false;
            sut.Completed += (_, _) => completed = true;

            sut.NextStepCommand.Execute(null);

            Assert.True(completed);
            Assert.Equal(SettingsRoute.Integrations, sut.CompletionRequest.SettingsRoute);
            Assert.Equal(plugin.PluginId, sut.CompletionRequest.PluginIdToConfigure);
        });
    }

    [Fact]
    public void FinalGetStarted_WithMissingDownloadableModel_RequestsDictationSettings()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.sherpa-onnx",
                "Parakeet",
                configured: true,
                supportsModelDownload: true,
                isModelDownloaded: false);
            var sut = CreateViewModel(plugin);
            sut.SelectedModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "whisper-large-v3-turbo");
            sut.CurrentStep = 3;
            var completed = false;
            sut.Completed += (_, _) => completed = true;

            sut.NextStepCommand.Execute(null);

            Assert.True(completed);
            Assert.Equal(SettingsRoute.Dictation, sut.CompletionRequest.SettingsRoute);
            Assert.Null(sut.CompletionRequest.PluginIdToConfigure);
        });
    }

    [Fact]
    public void Skip_DoesNotRequestSettings_WhenEngineIsNotReady()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: false,
                supportsModelDownload: false);
            var sut = CreateViewModel(plugin);
            sut.SelectedModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "whisper-large-v3-turbo");

            sut.SkipCommand.Execute(null);

            Assert.Null(sut.CompletionRequest.SettingsRoute);
            Assert.Null(sut.CompletionRequest.PluginIdToConfigure);
        });
    }

    [Fact]
    public void MainDictationHotkeys_AddAndRemovePersistsMultipleValues()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: true,
                supportsModelDownload: false);
            var sut = CreateViewModel(
                plugin,
                out _,
                out var settings,
                AppSettings.Default with
                {
                    MainDictationHotkeys = [],
                    PushToTalkHotkey = "",
                    ToggleHotkey = ""
                });

            sut.NewMainDictationHotkey = "Ctrl+Alt+D";
            sut.AddMainDictationHotkeyCommand.Execute(null);
            sut.NewMainDictationHotkey = "Ctrl+Shift+D";
            sut.AddMainDictationHotkeyCommand.Execute(null);
            sut.RemoveMainDictationHotkeyCommand.Execute("Ctrl+Alt+D");

            Assert.Equal(["Ctrl+Shift+D"], sut.MainDictationHotkeys);
            Assert.Equal(["Ctrl+Shift+D"], settings.Current.MainDictationHotkeys);
            Assert.Equal("Ctrl+Shift+D", settings.Current.PushToTalkHotkey);
        });
    }

    [Fact]
    public void MainDictationHotkeyCommand_AcceptsRecordedHotkeyParameter()
    {
        RunOnStaThread(() =>
        {
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: true,
                supportsModelDownload: false);
            var sut = CreateViewModel(
                plugin,
                out _,
                out var settings,
                AppSettings.Default with
                {
                    MainDictationHotkeys = [],
                    PushToTalkHotkey = "",
                    ToggleHotkey = ""
                });

            sut.AddMainDictationHotkeyCommand.Execute("Ctrl+Alt+D");

            Assert.Equal(["Ctrl+Alt+D"], sut.MainDictationHotkeys);
            Assert.Equal(["Ctrl+Alt+D"], settings.Current.MainDictationHotkeys);
            Assert.Equal("", sut.NewMainDictationHotkey);
        });
    }

    [Fact]
    public void MicTest_RaisesMicLevelOnWizardDispatcher()
    {
        RunOnStaThread(() =>
        {
            var uiThreadId = Environment.CurrentManagedThreadId;
            var plugin = new FakeTranscriptionPlugin(
                "com.typewhisper.groq",
                "Groq",
                configured: true,
                supportsModelDownload: false);
            var devices = new TypeWhisper.PluginSystem.Tests.FakeAudioInputDeviceProvider("USB Microphone");
            var captures = new TypeWhisper.PluginSystem.Tests.FakeAudioInputCaptureFactory();
            using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
            var sut = CreateViewModel(plugin, audio: audio);
            var changedThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WelcomeViewModel.MicLevel))
                    changedThread.TrySetResult(Environment.CurrentManagedThreadId);
            };

            sut.NextStepCommand.Execute(null);
            Assert.True(audio.IsRecording);

            var source = new short[] { 8192, 16384, 8192, 16384 };
            var bytes = new byte[source.Length * sizeof(short)];
            Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
            ThreadPool.QueueUserWorkItem(_ => captures.Created.Single().RaiseData(bytes, bytes.Length));
            PumpDispatcherUntil(changedThread.Task, TimeSpan.FromSeconds(2));

            Assert.True(changedThread.Task.IsCompleted);
            Assert.Equal(uiThreadId, changedThread.Task.Result);

            sut.NextStepCommand.Execute(null);
            Assert.False(audio.IsRecording);
        });
    }

    private static WelcomeViewModel CreateViewModel(FakeTranscriptionPlugin plugin) =>
        CreateViewModel(plugin, out _);

    private static WelcomeViewModel CreateViewModel(FakeTranscriptionPlugin plugin, AudioRecordingService audio) =>
        CreateViewModel(plugin, out _, out _, audio: audio);

    private static WelcomeViewModel CreateViewModel(
        FakeTranscriptionPlugin plugin,
        out PluginManager pluginManager)
    {
        return CreateViewModel(plugin, out pluginManager, out _);
    }

    private static WelcomeViewModel CreateViewModel(
        FakeTranscriptionPlugin plugin,
        out PluginManager pluginManager,
        out FakeSettingsService settings,
        AppSettings? initialSettings = null,
        AudioRecordingService? audio = null)
    {
        settings = new FakeSettingsService(initialSettings ?? AppSettings.Default);
        var workflows = new Mock<IWorkflowService>();
        workflows.Setup(w => w.Workflows).Returns([]);
        var activeWindow = new Mock<IActiveWindowService>();
        pluginManager = new PluginManager(
            new PluginLoader(),
            new PluginEventBus(),
            activeWindow.Object,
            workflows.Object,
            settings,
            []);

        var manifest = new PluginManifest
        {
            Id = plugin.PluginId,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "Fake.dll",
            PluginClass = plugin.GetType().FullName!
        };
        var loadContext = new PluginAssemblyLoadContext(typeof(WelcomeViewModelTests).Assembly.Location);
        var loadedPlugin = new LoadedPlugin(manifest, plugin, loadContext, AppContext.BaseDirectory);
        TestPluginManagerFactory.SetPrivateField(pluginManager, "_allPlugins", new List<LoadedPlugin> { loadedPlugin });
        TestPluginManagerFactory.SetPrivateField(pluginManager, "_activatedPlugins", new HashSet<string> { plugin.PluginId });
        TestPluginManagerFactory.InvokeRebuildCapabilityIndices(pluginManager);

        var modelManager = new ModelManagerService(pluginManager, settings);
        audio ??= new AudioRecordingService(
            new FakeAudioInputDeviceProvider(),
            new FakeAudioInputCaptureFactory(),
            Timeout.InfiniteTimeSpan);
        var registry = new PluginRegistryService(
            pluginManager,
            new PluginLoader(),
            settings,
            CreateRegistryHttpClient());
        var dictionary = new DictionaryViewModel(
            Mock.Of<IDictionaryService>(d => d.Entries == Array.Empty<DictionaryEntry>()),
            settings);
        var dictation = (DictationViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DictationViewModel));

        return new WelcomeViewModel(modelManager, settings, audio, registry, dictation, dictionary);
    }

    private static HttpClient CreateRegistryHttpClient()
    {
        var handler = new StaticHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        return new HttpClient(handler);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw failure;
    }

    private static void PumpDispatcherUntil(Task task, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            string providerDisplayName,
            bool configured,
            bool supportsModelDownload,
            bool isModelDownloaded = true)
        {
            PluginId = pluginId;
            ProviderDisplayName = providerDisplayName;
            IsConfigured = configured;
            SupportsModelDownload = supportsModelDownload;
            ModelDownloaded = isModelDownloaded;
        }

        public string PluginId { get; }
        public string PluginName => ProviderDisplayName;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName { get; }
        public bool IsConfigured { get; set; }
        public bool SupportsModelDownload { get; }
        public bool ModelDownloaded { get; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [
            new("whisper-large-v3-turbo", "Whisper Large V3 Turbo")
        ];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public int CreateSettingsViewCallCount { get; private set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView()
        {
            CreateSettingsViewCallCount++;
            return new UserControl { Tag = PluginId };
        }

        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public bool IsModelDownloaded(string modelId) => !SupportsModelDownload || ModelDownloaded;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));

        public void Dispose() { }
    }

    private sealed class FakeAudioInputDeviceProvider : IAudioInputDeviceProvider
    {
        public int DeviceCount => 0;
        public string GetDeviceName(int deviceNumber) => throw new ArgumentOutOfRangeException(nameof(deviceNumber));
    }

    private sealed class FakeAudioInputCaptureFactory : IAudioInputCaptureFactory
    {
        public IAudioInputCapture Create(int deviceNumber, NAudio.Wave.WaveFormat waveFormat, int bufferMilliseconds) =>
            throw new InvalidOperationException("No capture should be created when no input device exists.");
    }

    private sealed class StaticHttpMessageHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory());
    }
}
