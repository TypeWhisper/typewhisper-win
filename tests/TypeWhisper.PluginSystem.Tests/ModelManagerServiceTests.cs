using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class ModelManagerServiceTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public ModelManagerServiceTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
    }

    [Fact]
    public void Engine_WithoutActiveModel_DoesNotFallbackToArbitraryConfiguredPlugin()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = ModelManagerService.GetPluginModelId("com.typewhisper.sherpa-onnx", "parakeet")
        });

        var pluginManager = CreatePluginManager(
            new FakeTranscriptionPlugin("com.typewhisper.openai-compatible", configured: true, selectedModelId: "whisper"),
            new FakeTranscriptionPlugin("com.typewhisper.sherpa-onnx", configured: true, selectedModelId: null));

        var sut = new ModelManagerService(pluginManager, _settings.Object);

        Assert.IsType<NoOpTranscriptionEngine>(sut.Engine);
        Assert.False(sut.Engine.IsModelLoaded);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_LoadsSelectedModel_WhenNoActiveModelExists()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal(modelId, plugin.SelectedModelId);
        Assert.Equal(modelId, plugin.LastLoadedModelId);
        Assert.True(sut.Engine.IsModelLoaded);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_ReloadsActiveModel_WhenAccelerationPreferenceChanges()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var currentSettings = new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        };

        _settings.Setup(s => s.Current).Returns(() => currentSettings);

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await sut.EnsureModelLoadedAsync();
        currentSettings = currentSettings with
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
        };

        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(2, plugin.LoadCallCount);
        Assert.Equal(
            [TranscriptionAccelerationPreference.Cpu, TranscriptionAccelerationPreference.NvidiaCuda],
            plugin.AccelerationPreferencesAtLoad);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_DoesNotReloadActiveModel_WhenAccelerationPreferenceIsUnchanged()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            AccelerationStatusOverride = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.NvidiaCuda,
                "Using CUDA")
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await sut.EnsureModelLoadedAsync();
        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(1, plugin.LoadCallCount);
        Assert.Equal(
            [TranscriptionAccelerationPreference.NvidiaCuda],
            plugin.AccelerationPreferencesAtLoad);
    }

    [Theory]
    [InlineData(
        AppSettings.LocalModelAccelerationNvidiaCuda,
        TranscriptionAccelerationPreference.NvidiaCuda,
        "CUDA unavailable")]
    [InlineData(
        AppSettings.LocalModelAccelerationAmdVulkan,
        TranscriptionAccelerationPreference.AmdVulkan,
        "Vulkan unavailable")]
    [InlineData(
        AppSettings.LocalModelAccelerationAmdRocm,
        TranscriptionAccelerationPreference.AmdRocm,
        "ROCm unavailable")]
    public async Task EnsureModelLoadedAsync_ReloadsExplicitActiveModel_WhenBackendIsCpu(
        string savedAcceleration,
        TranscriptionAccelerationPreference expectedPreference,
        string displayText)
    {
        const string pluginId = "com.typewhisper.whisper-cpp";
        const string modelId = "whisper";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = savedAcceleration
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            AccelerationStatusOverride = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                displayText,
                "Requested acceleration was not active after the model had already loaded.")
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await sut.EnsureModelLoadedAsync();
        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(2, plugin.LoadCallCount);
        Assert.Equal(
            [expectedPreference, expectedPreference],
            plugin.AccelerationPreferencesAtLoad);
    }

    [Theory]
    [InlineData(AppSettings.LocalModelAccelerationAuto, TranscriptionAccelerationPreference.Auto)]
    [InlineData(AppSettings.LocalModelAccelerationCpu, TranscriptionAccelerationPreference.Cpu)]
    [InlineData(AppSettings.LocalModelAccelerationNvidiaCuda, TranscriptionAccelerationPreference.NvidiaCuda)]
    [InlineData(AppSettings.LocalModelAccelerationAmdVulkan, TranscriptionAccelerationPreference.AmdVulkan)]
    [InlineData(AppSettings.LocalModelAccelerationAmdRocm, TranscriptionAccelerationPreference.AmdRocm)]
    [InlineData("CUDA", TranscriptionAccelerationPreference.NvidiaCuda)]
    [InlineData("vulkan", TranscriptionAccelerationPreference.AmdVulkan)]
    [InlineData("hip", TranscriptionAccelerationPreference.AmdRocm)]
    [InlineData("directml", TranscriptionAccelerationPreference.Auto)]
    public async Task LoadModelAsync_AppliesSavedAccelerationPreferenceBeforeLoading(
        string savedAcceleration,
        TranscriptionAccelerationPreference expectedPreference)
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LocalModelAcceleration = savedAcceleration
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(expectedPreference, plugin.LastAccelerationPreference);
        Assert.Equal(expectedPreference, plugin.AccelerationPreferenceAtLoad);
    }

    [Theory]
    [InlineData("Vulkan unavailable", AppSettings.LocalModelAccelerationAmdVulkan)]
    [InlineData("ROCm unavailable", AppSettings.LocalModelAccelerationAmdRocm)]
    public async Task LoadModelAsync_UsesCompactAccelerationUnavailableModelStatusOnExplicitFailure(
        string displayText,
        string savedAcceleration)
    {
        const string pluginId = "com.typewhisper.whisper-cpp";
        const string modelId = "whisper";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LocalModelAcceleration = savedAcceleration
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            AccelerationStatusOverride = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                displayText,
                $"{displayText}: native runtime could not be loaded."),
            LoadException = new InvalidOperationException($"{displayText}: native runtime could not be loaded.")
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.LoadModelAsync(fullModelId));

        var status = sut.GetStatus(fullModelId);
        Assert.Equal(ModelStatusType.Error, status.Type);
        Assert.Equal(displayText, status.ErrorMessage);
    }

    [Fact]
    public async Task LoadModelAsync_UsesCompactCudaUnavailableModelStatusOnCudaLoadFailure()
    {
        const string pluginId = "com.typewhisper.whisper-cpp";
        const string modelId = "whisper";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            AccelerationStatusOverride = new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "CUDA unavailable",
                "CUDA runtime could not be loaded. Missing CUDA/cuBLAS runtime dependency cublas64_13.dll."),
            LoadException = new InvalidOperationException(
                "CUDA runtime could not be loaded. Missing CUDA/cuBLAS runtime dependency cublas64_13.dll.")
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.LoadModelAsync(fullModelId));

        var status = sut.GetStatus(fullModelId);
        Assert.Equal(ModelStatusType.Error, status.Type);
        Assert.Equal("CUDA unavailable", status.ErrorMessage);
    }

    private PluginManager CreatePluginManager(params ITranscriptionEnginePlugin[] transcriptionEngines)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            _settings.Object,
            []);

        SetPrivateField(pluginManager, "_transcriptionEngines", transcriptionEngines.ToList());
        return pluginManager;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            bool configured,
            string? selectedModelId,
            bool supportsModelDownload = false)
        {
            PluginId = pluginId;
            IsConfigured = configured;
            SelectedModelId = selectedModelId;
            SupportsModelDownload = supportsModelDownload;
            TranscriptionModels = [new PluginModelInfo("parakeet", "Parakeet"), new PluginModelInfo("whisper", "Whisper")];
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName => PluginId;
        public bool IsConfigured { get; }
        public bool SupportsModelDownload { get; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public string? LastLoadedModelId { get; private set; }
        public int LoadCallCount { get; private set; }
        public List<TranscriptionAccelerationPreference> AccelerationPreferencesAtLoad { get; } = [];
        public TranscriptionAccelerationPreference LastAccelerationPreference { get; private set; } =
            TranscriptionAccelerationPreference.Auto;
        public TranscriptionAccelerationPreference? AccelerationPreferenceAtLoad { get; private set; }
        public TranscriptionAccelerationStatus? AccelerationStatusOverride { get; init; }
        public TranscriptionAccelerationStatus AccelerationStatus => AccelerationStatusOverride
            ?? new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Cpu, "Using CPU");
        public Exception? LoadException { get; init; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public void SetAccelerationPreference(TranscriptionAccelerationPreference preference) =>
            LastAccelerationPreference = preference;

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCallCount++;
            AccelerationPreferenceAtLoad = LastAccelerationPreference;
            AccelerationPreferencesAtLoad.Add(LastAccelerationPreference);
            if (LoadException is not null)
                throw LoadException;

            LastLoadedModelId = modelId;
            SelectedModelId = modelId;
            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));

        public void Dispose() { }
    }
}
