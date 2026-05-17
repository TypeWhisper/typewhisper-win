using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
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

    private static WelcomeViewModel CreateViewModel(FakeTranscriptionPlugin plugin) =>
        CreateViewModel(plugin, out _);

    private static WelcomeViewModel CreateViewModel(
        FakeTranscriptionPlugin plugin,
        out PluginManager pluginManager)
    {
        var settings = new FakeSettingsService(AppSettings.Default);
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
        var audio = new AudioRecordingService(
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

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            string providerDisplayName,
            bool configured,
            bool supportsModelDownload)
        {
            PluginId = pluginId;
            ProviderDisplayName = providerDisplayName;
            IsConfigured = configured;
            SupportsModelDownload = supportsModelDownload;
        }

        public string PluginId { get; }
        public string PluginName => ProviderDisplayName;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName { get; }
        public bool IsConfigured { get; set; }
        public bool SupportsModelDownload { get; }
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
