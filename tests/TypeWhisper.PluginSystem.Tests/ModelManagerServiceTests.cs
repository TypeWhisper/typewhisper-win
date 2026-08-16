using System.IO;
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
    public async Task LoadModelAsync_SwitchingTranscriptionPlugin_UnloadsPreviousPlugin()
    {
        const string localPluginId = "com.typewhisper.sherpa-onnx";
        const string localModelId = "parakeet";
        const string cloudPluginId = "com.typewhisper.cloud";
        const string cloudModelId = "whisper";
        var localFullModelId = ModelManagerService.GetPluginModelId(localPluginId, localModelId);
        var cloudFullModelId = ModelManagerService.GetPluginModelId(cloudPluginId, cloudModelId);
        var unloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycleEvents = new List<string>();

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = cloudFullModelId
        });

        var localPlugin = new FakeTranscriptionPlugin(
            localPluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [localModelId])
        {
            UnloadStarted = unloadStarted,
            UnloadGate = unloadGate,
            LifecycleEvents = lifecycleEvents
        };
        var cloudPlugin = new FakeTranscriptionPlugin(
            cloudPluginId,
            configured: true,
            selectedModelId: null,
            modelIds: [cloudModelId])
        {
            LifecycleEvents = lifecycleEvents
        };
        var sut = new ModelManagerService(
            CreatePluginManager(localPlugin, cloudPlugin),
            _settings.Object);

        await sut.LoadModelAsync(localFullModelId);
        lifecycleEvents.Clear();

        var switchTask = sut.LoadModelAsync(cloudFullModelId);
        await unloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(switchTask.IsCompleted);
        Assert.Null(cloudPlugin.SelectedModelId);

        unloadGate.SetResult();
        await switchTask;

        Assert.Equal(1, localPlugin.UnloadCallCount);
        Assert.Equal(
            [$"{localPluginId}:unload", $"{cloudPluginId}:select"],
            lifecycleEvents);
        Assert.Equal(cloudFullModelId, sut.ActiveModelId);
        Assert.Same(cloudPlugin, sut.ActiveTranscriptionPlugin);
    }

    [Fact]
    public async Task LoadModelAsync_SwitchingTranscriptionPlugin_ClearsActiveStateWhenUnloadFails()
    {
        const string localPluginId = "com.typewhisper.sherpa-onnx";
        const string localModelId = "parakeet";
        const string cloudPluginId = "com.typewhisper.cloud";
        const string cloudModelId = "whisper";
        var localFullModelId = ModelManagerService.GetPluginModelId(localPluginId, localModelId);
        var cloudFullModelId = ModelManagerService.GetPluginModelId(cloudPluginId, cloudModelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = cloudFullModelId
        });

        var localPlugin = new FakeTranscriptionPlugin(
            localPluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [localModelId])
        {
            UnloadException = new InvalidOperationException("Unload failed")
        };
        var cloudPlugin = new FakeTranscriptionPlugin(
            cloudPluginId,
            configured: true,
            selectedModelId: null,
            modelIds: [cloudModelId]);
        var sut = new ModelManagerService(
            CreatePluginManager(localPlugin, cloudPlugin),
            _settings.Object);

        await sut.LoadModelAsync(localFullModelId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.LoadModelAsync(cloudFullModelId));

        Assert.Equal("Unload failed", exception.Message);
        Assert.Equal(1, localPlugin.UnloadCallCount);
        Assert.Null(cloudPlugin.SelectedModelId);
        Assert.Null(sut.ActiveModelId);
        Assert.Null(sut.ActiveTranscriptionPlugin);
    }

    [Fact]
    public async Task PluginInstanceReplacement_InvalidatesAndReloadsActiveModel()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var originalPlugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var replacementPlugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(originalPlugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);
        await sut.LoadModelAsync(fullModelId);

        SetTranscriptionEnginesAndNotify(pluginManager, replacementPlugin);

        Assert.Null(sut.ActiveModelId);
        Assert.IsType<NoOpTranscriptionEngine>(sut.Engine);

        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal(1, originalPlugin.LoadCallCount);
        Assert.Equal(1, replacementPlugin.LoadCallCount);
        Assert.Equal(modelId, replacementPlugin.SelectedModelId);
    }

    [Fact]
    public async Task PluginInstanceReplacement_DuringLoadCannotPublishRemovedPlugin()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalPlugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            LoadGate = loadGate
        };
        var replacementPlugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(originalPlugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        var loadTask = sut.LoadModelAsync(fullModelId);
        Assert.Equal(1, originalPlugin.LoadCallCount);
        SetTranscriptionEnginesAndNotify(pluginManager, replacementPlugin);
        loadGate.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => loadTask);

        Assert.Null(sut.ActiveModelId);
        Assert.IsType<NoOpTranscriptionEngine>(sut.Engine);
        Assert.NotEqual(ModelStatusType.Ready, sut.GetStatus(fullModelId).Type);

        Assert.True(await sut.EnsureModelLoadedAsync());
        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal(1, replacementPlugin.LoadCallCount);
    }

    [Fact]
    public async Task PluginRemoval_InvalidatesActiveModelAndCannotReportLoaded()
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
        await sut.LoadModelAsync(fullModelId);

        SetTranscriptionEnginesAndNotify(pluginManager);

        Assert.Null(sut.ActiveModelId);
        Assert.IsType<NoOpTranscriptionEngine>(sut.Engine);
        Assert.Equal(ModelStatusType.NotDownloaded, sut.GetStatus(fullModelId).Type);
        Assert.False(await sut.EnsureModelLoadedAsync());
    }

    [Fact]
    public async Task Dispose_StopsObservingPluginInstanceReplacement()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(s => s.Current).Returns(new AppSettings());

        var originalPlugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var replacementPlugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(originalPlugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);
        await sut.LoadModelAsync(fullModelId);
        sut.Dispose();

        SetTranscriptionEnginesAndNotify(pluginManager, replacementPlugin);

        Assert.Equal(fullModelId, sut.ActiveModelId);
    }

    [Fact]
    public async Task LoadModelAsync_UsesTranscriptionSelectionIdForAdditionalProfileRole()
    {
        const string rootPluginId = "com.typewhisper.openai-compatible";
        const string selectionId = "openai-compatible-profile-a";
        const string modelId = "whisper-profile";
        var fullModelId = ModelManagerService.GetPluginModelId(selectionId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings());

        var plugin = new FakeTranscriptionPlugin(
            rootPluginId,
            configured: true,
            selectedModelId: null,
            selectionId: selectionId,
            modelIds: [modelId]);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await sut.LoadModelAsync(fullModelId);

        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal(modelId, plugin.SelectedModelId);
    }

    [Fact]
    public void ResolveTranscriptionIdentity_UsesProviderIdAndRawModelId()
    {
        const string providerId = "com.typewhisper.openai-compatible";
        const string selectionId = "openai-compatible-profile-a";
        const string modelId = "whisper-profile";
        var plugin = new FakeTranscriptionPlugin(
            providerId,
            configured: true,
            selectedModelId: modelId,
            selectionId: selectionId,
            modelIds: [modelId]);
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);

        var identity = sut.ResolveTranscriptionIdentity(
            ModelManagerService.GetPluginModelId(selectionId, modelId));

        Assert.NotNull(identity);
        Assert.Equal(providerId, identity.EngineId);
        Assert.Equal(modelId, identity.ModelId);
    }

    [Theory]
    [InlineData("plugin:selection:")]
    [InlineData("plugin:selection:   ")]
    [InlineData("plugin::model")]
    [InlineData("plugin:   :model")]
    public void ResolveTranscriptionIdentity_RejectsBlankComponents(string fullModelId)
    {
        var plugin = new FakeTranscriptionPlugin(
            "provider",
            configured: true,
            selectedModelId: "model",
            selectionId: "selection",
            modelIds: ["model"]);
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);

        Assert.Null(sut.ResolveTranscriptionIdentity(fullModelId));
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

    [Fact]
    public async Task DownloadAndLoadModelAsync_AppliesSavedAccelerationPreferenceBeforeDownloading()
    {
        const string pluginId = "com.typewhisper.cohere-transcribe";
        const string modelId = "cohere-transcribe-03-2026-q5_0";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            ModelDownloaded = false
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        await sut.DownloadAndLoadModelAsync(fullModelId);

        Assert.Equal(1, plugin.DownloadCallCount);
        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            plugin.AccelerationPreferenceAtDownload);
        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            plugin.AccelerationPreferenceAtLoad);
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

    [Fact]
    public void GetStatus_ReturnsError_WhenPluginDownloadStatusThrows()
    {
        const string pluginId = "com.typewhisper.granite-speech";
        const string modelId = "granite";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            DownloadStatusException = new MissingMethodException(
                typeof(IPluginHostServices).FullName,
                "PluginAssetDirectory")
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        var status = sut.GetStatus(fullModelId);
        var isDownloaded = sut.IsDownloaded(fullModelId);

        Assert.Equal(ModelStatusType.Error, status.Type);
        Assert.Contains("PluginAssetDirectory", status.ErrorMessage);
        Assert.False(isDownloaded);
    }

    [Fact]
    public void IsDownloaded_DoesNotRaiseRepeatedStatusChanges_WhenPluginDownloadStatusThrows()
    {
        const string pluginId = "com.typewhisper.granite-speech";
        const string modelId = "granite";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true)
        {
            DownloadStatusException = new MissingMethodException(
                typeof(IPluginHostServices).FullName,
                "PluginAssetDirectory")
        };
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);
        var statusChangedCount = 0;

        sut.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ModelManagerService.GetStatus))
                return;

            statusChangedCount++;
            if (statusChangedCount < 3)
                sut.IsDownloaded(fullModelId);
        };

        Assert.False(sut.IsDownloaded(fullModelId));

        Assert.Equal(1, statusChangedCount);
    }

    [Fact]
    public async Task RemoveModelAsync_UnloadsDeletesAndClearsSelectedModel()
    {
        const string pluginId = "com.typewhisper.local";
        const string modelId = "local-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new AppSettings { SelectedModelId = fullModelId };
        _settings.Setup(service => service.Current).Returns(settings);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            SupportsModelRemoval = true,
            ModelDownloaded = true
        };
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);
        await sut.LoadModelAsync(fullModelId);

        await sut.RemoveModelAsync(fullModelId);

        Assert.Null(sut.ActiveModelId);
        Assert.Equal(1, plugin.UnloadCallCount);
        Assert.Equal(1, plugin.RemoveCallCount);
        Assert.False(plugin.ModelDownloaded);
        Assert.Equal(ModelStatusType.NotDownloaded, sut.GetStatus(fullModelId).Type);
        _settings.Verify(service => service.Save(
            It.Is<AppSettings>(value => value.SelectedModelId == null)), Times.Once);
    }

    [Fact]
    public async Task RemoveModelAsync_RejectsPluginsWithoutRemovalCapability()
    {
        const string pluginId = "com.typewhisper.legacy-local";
        const string modelId = "legacy-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId]);
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);

        Assert.False(sut.SupportsModelRemoval(fullModelId));
        await Assert.ThrowsAsync<NotSupportedException>(() => sut.RemoveModelAsync(fullModelId));
        Assert.Equal(0, plugin.RemoveCallCount);
    }

    [Fact]
    public async Task DownloadAndLoadModelAsync_FailedDownloadPublishesRetryableErrorState()
    {
        const string pluginId = "com.typewhisper.retry-local";
        const string modelId = "retry-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            ModelDownloaded = false,
            DownloadException = new IOException("download interrupted")
        };
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);

        await Assert.ThrowsAsync<IOException>(() => sut.DownloadAndLoadModelAsync(fullModelId));

        var failed = sut.GetStatus(fullModelId);
        Assert.Equal(ModelStatusType.Error, failed.Type);
        Assert.Equal("download interrupted", failed.ErrorMessage);
        Assert.Equal(1, plugin.DownloadCallCount);

        plugin.DownloadException = null;
        await sut.DownloadAndLoadModelAsync(fullModelId);

        Assert.Equal(2, plugin.DownloadCallCount);
        Assert.Equal(ModelStatusType.Ready, sut.GetStatus(fullModelId).Type);
    }

    [Fact]
    public async Task DownloadAndLoadModelAsync_BlocksUnsatisfiedRequiredDownloadRequirement()
    {
        const string pluginId = "com.typewhisper.licensed-local";
        const string modelId = "licensed-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            ModelDownloaded = false,
            ModelDownloadRequirements =
            [
                new PluginModelDownloadRequirement(
                    modelId,
                    "Licensed model",
                    "license",
                    PluginModelDownloadRequirementKind.License,
                    "Model license",
                    "Accept the model license.",
                    IsRequired: true,
                    IsSatisfied: false)
            ]
        };
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DownloadAndLoadModelAsync(fullModelId));

        Assert.Contains("Model license", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, plugin.DownloadCallCount);
        Assert.Equal(0, plugin.LoadCallCount);
        Assert.Same(plugin, sut.GetModelDownloadRequirementsProvider(fullModelId));
        Assert.Single(sut.GetModelDownloadRequirements(fullModelId));
    }

    [Fact]
    public async Task ModelOperations_RejectConcurrentOperationsForSamePlugin()
    {
        const string pluginId = "com.typewhisper.serial-local";
        const string modelId = "serial-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            LoadGate = loadGate,
            LoadStarted = loadStarted,
            SupportsModelRemoval = true
        };
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);
        var firstOperation = sut.LoadModelAsync(fullModelId);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RemoveModelAsync(fullModelId));

        Assert.Contains(plugin.ProviderDisplayName, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, plugin.RemoveCallCount);

        loadGate.SetResult();
        await firstOperation;
        Assert.Equal(1, plugin.LoadCallCount);
    }

    [Fact]
    public async Task Dispose_DoesNotBreakInFlightModelOperationLease()
    {
        const string pluginId = "com.typewhisper.disposing-local";
        const string modelId = "disposing-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            LoadGate = loadGate,
            LoadStarted = loadStarted
        };
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);
        var loadTask = sut.LoadModelAsync(fullModelId);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sut.Dispose();
        loadGate.SetResult();

        await loadTask;
        Assert.Equal(fullModelId, sut.ActiveModelId);
    }

    [Fact]
    public async Task RemoveModelAsync_PreservesCancellationWhenStatusCheckFails()
    {
        const string pluginId = "com.typewhisper.cancelling-local";
        const string modelId = "cancelling-model";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        using var cancellation = new CancellationTokenSource();
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true,
            modelIds: [modelId])
        {
            SupportsModelRemoval = true,
            ModelDownloaded = true,
            DownloadStatusException = new MissingMethodException("status failed"),
            RemoveAction = ct =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(ct);
            }
        };
        var sut = new ModelManagerService(CreatePluginManager(plugin), _settings.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RemoveModelAsync(fullModelId, cancellation.Token));

        Assert.Equal(ModelStatusType.NotDownloaded, sut.GetStatus(fullModelId).Type);
    }

    [Theory]
    [InlineData(true, "TypeWhisper, ElevenLabs")]
    [InlineData(false, null)]
    public async Task Engine_PassesDictionaryTermsOnlyToOptInPlugin(
        bool supportsDictionaryTerms,
        string? expectedPrompt)
    {
        const string pluginId = "com.typewhisper.test";
        const string modelId = "whisper";
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            modelIds: [modelId])
        {
            SupportsDictionaryTerms = supportsDictionaryTerms
        };
        var dictionary = new Mock<IDictionaryService>();
        dictionary.Setup(service => service.GetEnabledTerms())
            .Returns(["TypeWhisper", "ElevenLabs"]);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object, dictionary.Object);

        await sut.LoadModelAsync(ModelManagerService.GetPluginModelId(pluginId, modelId));
        await sut.Engine.TranscribeAsync([0f, 0f], "en");

        Assert.Equal(expectedPrompt, plugin.LastPrompt);
    }

    [Fact]
    public async Task Engine_ClipsDictionaryTermsToPluginBudget()
    {
        const string pluginId = "com.typewhisper.test";
        const string modelId = "whisper";
        _settings.Setup(service => service.Current).Returns(new AppSettings());
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            modelIds: [modelId])
        {
            SupportsDictionaryTerms = true,
            DictionaryTermsBudget = new(MaxTerms: 2, MaxCharsPerTerm: 5, MaxWordsPerTerm: 1)
        };
        var dictionary = new Mock<IDictionaryService>();
        dictionary.Setup(service => service.GetEnabledTerms())
            .Returns(["a b", "Alpha", "Beta", "Gamma"]);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object, dictionary.Object);

        await sut.LoadModelAsync(ModelManagerService.GetPluginModelId(pluginId, modelId));
        await sut.Engine.TranscribeAsync([0f, 0f], "en");

        Assert.Equal("Alpha, Beta", plugin.LastPrompt);
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

    private static void SetTranscriptionEnginesAndNotify(
        PluginManager pluginManager,
        params ITranscriptionEnginePlugin[] transcriptionEngines)
    {
        SetPrivateField(pluginManager, "_transcriptionEngines", transcriptionEngines.ToList());
        var eventField = typeof(PluginManager).GetField(
            nameof(PluginManager.PluginStateChanged),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PluginManager).FullName, nameof(PluginManager.PluginStateChanged));
        ((EventHandler?)eventField.GetValue(pluginManager))?.Invoke(pluginManager, EventArgs.Empty);
    }

    private sealed class FakeTranscriptionPlugin :
        ITranscriptionEnginePlugin,
        ITranscriptionEngineSelectionIdentity,
        IModelDownloadRequirementsProvider
    {
        private readonly string? _selectionId;

        public FakeTranscriptionPlugin(
            string pluginId,
            bool configured,
            string? selectedModelId,
            bool supportsModelDownload = false,
            string? selectionId = null,
            IReadOnlyList<string>? modelIds = null)
        {
            PluginId = pluginId;
            IsConfigured = configured;
            SelectedModelId = selectedModelId;
            SupportsModelDownload = supportsModelDownload;
            _selectionId = selectionId;
            TranscriptionModels = modelIds is null
                ? [new PluginModelInfo("parakeet", "Parakeet"), new PluginModelInfo("whisper", "Whisper")]
                : modelIds.Select(modelId => new PluginModelInfo(modelId, modelId)).ToList();
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string TranscriptionSelectionId => _selectionId ?? PluginId;
        public string ProviderId => PluginId;
        public string ProviderDisplayName => PluginId;
        public bool IsConfigured { get; }
        public bool SupportsModelDownload { get; }
        public bool SupportsModelRemoval { get; init; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public bool SupportsDictionaryTerms { get; init; }
        public DictionaryTermsBudget DictionaryTermsBudget { get; init; } = DictionaryTermsBudget.Default;
        public string? LastPrompt { get; private set; }
        public string? LastLoadedModelId { get; private set; }
        public int LoadCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public int RemoveCallCount { get; private set; }
        public int UnloadCallCount { get; private set; }
        public List<TranscriptionAccelerationPreference> AccelerationPreferencesAtLoad { get; } = [];
        public TranscriptionAccelerationPreference LastAccelerationPreference { get; private set; } =
            TranscriptionAccelerationPreference.Auto;
        public TranscriptionAccelerationPreference? AccelerationPreferenceAtDownload { get; private set; }
        public TranscriptionAccelerationPreference? AccelerationPreferenceAtLoad { get; private set; }
        public TranscriptionAccelerationStatus? AccelerationStatusOverride { get; init; }
        public TranscriptionAccelerationStatus AccelerationStatus => AccelerationStatusOverride
            ?? new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Cpu, "Using CPU");
        public Exception? LoadException { get; init; }
        public Exception? DownloadException { get; set; }
        public TaskCompletionSource? LoadGate { get; init; }
        public Exception? UnloadException { get; init; }
        public TaskCompletionSource? UnloadStarted { get; init; }
        public TaskCompletionSource? UnloadGate { get; init; }
        public List<string>? LifecycleEvents { get; init; }
        public Exception? DownloadStatusException { get; init; }
        public bool ModelDownloaded { get; set; } = true;
        public IReadOnlyList<PluginModelDownloadRequirement> ModelDownloadRequirements { get; init; } = [];
        public event EventHandler? ModelDownloadRequirementsChanged
        {
            add { }
            remove { }
        }
        public TaskCompletionSource? LoadStarted { get; init; }
        public Action<CancellationToken>? RemoveAction { get; init; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId)
        {
            SelectedModelId = modelId;
            LifecycleEvents?.Add($"{PluginId}:select");
        }
        public void SetAccelerationPreference(TranscriptionAccelerationPreference preference) =>
            LastAccelerationPreference = preference;
        public bool IsModelDownloaded(string modelId)
        {
            if (DownloadStatusException is not null)
                throw DownloadStatusException;

            return ModelDownloaded;
        }

        public Task DownloadModelAsync(
            string modelId,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            DownloadCallCount++;
            AccelerationPreferenceAtDownload = LastAccelerationPreference;
            progress?.Report(1);
            if (DownloadException is not null)
                throw DownloadException;

            ModelDownloaded = true;
            return Task.CompletedTask;
        }

        public Task RemoveModelAsync(string modelId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RemoveCallCount++;
            RemoveAction?.Invoke(ct);
            ModelDownloaded = false;
            return Task.CompletedTask;
        }

        public async Task UnloadModelAsync()
        {
            UnloadCallCount++;
            UnloadStarted?.TrySetResult();
            if (UnloadGate is not null)
                await UnloadGate.Task;

            if (UnloadException is not null)
                throw UnloadException;

            LifecycleEvents?.Add($"{PluginId}:unload");
        }

        public async Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCallCount++;
            LoadStarted?.TrySetResult();
            AccelerationPreferenceAtLoad = LastAccelerationPreference;
            AccelerationPreferencesAtLoad.Add(LastAccelerationPreference);
            if (LoadException is not null)
                throw LoadException;

            if (LoadGate is not null)
                await LoadGate.Task.WaitAsync(ct);

            LastLoadedModelId = modelId;
            SelectedModelId = modelId;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));
        }

        public void Dispose() { }
    }
}
