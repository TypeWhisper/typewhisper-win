using System.IO;
using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class ModelManagerViewModelTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public ModelManagerViewModelTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
    }

    [Fact]
    public void Constructor_UsesSavedSelection_WhenNoModelIsActive()
    {
        const string pluginId = "com.typewhisper.groq";
        const string modelId = "whisper-large-v3";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(pluginId, "Groq", modelId, "Whisper Large V3", configured: true));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.Equal(fullModelId, sut.SelectedModelOptionId);
        Assert.Equal("Groq", sut.ActiveProviderDisplayName);
        Assert.Equal("Whisper Large V3", sut.ActiveModelDisplayName);
    }

    [Fact]
    public void Constructor_ExposesAccelerationOptionsAndUsesSavedSelection()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
        });

        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, sut.SelectedAccelerationOptionValue);
        Assert.Contains(sut.AccelerationOptions, o => o.Value == AppSettings.LocalModelAccelerationAuto);
        Assert.Contains(sut.AccelerationOptions, o => o.Value == AppSettings.LocalModelAccelerationCpu);
        Assert.Contains(sut.AccelerationOptions, o => o.Value == AppSettings.LocalModelAccelerationNvidiaCuda);
        Assert.Contains(sut.AccelerationOptions, o => o.Value == AppSettings.LocalModelAccelerationAmdVulkan);
        Assert.Contains(sut.AccelerationOptions, o => o.Value == AppSettings.LocalModelAccelerationAmdRocm);
    }

    [Fact]
    public void SelectedAccelerationOptionValue_StoresNormalizedSetting()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
        });

        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        sut.SelectedAccelerationOptionValue = "CUDA";

        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, settings.Current.LocalModelAcceleration);
        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, sut.SelectedAccelerationOptionValue);
    }

    [Fact]
    public void SelectedAccelerationOptionValue_StoresAmdRocmAliasAsNormalizedSetting()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
        });

        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        sut.SelectedAccelerationOptionValue = "hip";

        Assert.Equal(AppSettings.LocalModelAccelerationAmdRocm, settings.Current.LocalModelAcceleration);
        Assert.Equal(AppSettings.LocalModelAccelerationAmdRocm, sut.SelectedAccelerationOptionValue);
    }

    [Fact]
    public void SelectedAccelerationOptionValue_AppliesPreferenceToSelectedPlugin()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationAuto
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);

        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationCpu;

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.LastAccelerationPreference);
    }

    [Fact]
    public async Task SelectedAccelerationOptionValue_ReloadsActiveModel_WhenAccelerationChanges()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);

        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        await modelManager.LoadModelAsync(fullModelId);

        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationNvidiaCuda;

        Assert.True(
            await plugin.WaitForLoadCountAsync(2, TimeSpan.FromSeconds(1)),
            "Changing acceleration for the active model should reload it immediately.");
        Assert.True(
            await WaitForConditionAsync(() => !sut.IsBusy, TimeSpan.FromSeconds(1)),
            "The acceleration reload should clear the busy state after it finishes.");
        Assert.Equal(
            [TranscriptionAccelerationPreference.Cpu, TranscriptionAccelerationPreference.NvidiaCuda],
            plugin.AccelerationPreferencesAtLoad);
        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, settings.Current.LocalModelAcceleration);
    }

    [Fact]
    public async Task SelectedAccelerationOptionValue_ShowsRestartPrompt_WhenProviderRequiresRestart()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true,
            accelerationStatusFactory: preference => preference == TranscriptionAccelerationPreference.NvidiaCuda
                ? new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "Restart TypeWhisper to switch sherpa-onnx to CUDA.",
                    RequiresRestart: true)
                : new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU"));
        var notifications = new FakeNotificationService();

        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(
            modelManager,
            settings,
            new FakeAppRestartService(),
            notifications);

        await modelManager.LoadModelAsync(fullModelId);
        plugin.LoadException = new InvalidOperationException("Restart TypeWhisper to switch sherpa-onnx to CUDA.");

        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationNvidiaCuda;

        Assert.True(
            await WaitForConditionAsync(() => sut.IsAccelerationRestartRequired, TimeSpan.FromSeconds(1)),
            "Acceleration changes that require a process restart should show a persistent restart prompt.");
        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, settings.Current.LocalModelAcceleration);
        Assert.Contains("Restart", sut.AccelerationStatusText, StringComparison.OrdinalIgnoreCase);
        var notification = Assert.Single(notifications.Messages);
        Assert.NotNull(notification.OnClick);
    }

    [Fact]
    public async Task SelectedAccelerationOptionValue_ClearsRestartPrompt_WhenLatestSelectionLoadsWithoutRestart()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true,
            accelerationStatusFactory: preference => preference == TranscriptionAccelerationPreference.NvidiaCuda
                ? new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU",
                    "Restart TypeWhisper to switch sherpa-onnx to CUDA.",
                    RequiresRestart: true)
                : new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.Cpu,
                    "Using CPU"));

        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(
            modelManager,
            settings,
            new FakeAppRestartService(),
            new FakeNotificationService());

        await modelManager.LoadModelAsync(fullModelId);
        plugin.LoadException = new InvalidOperationException("Restart TypeWhisper to switch sherpa-onnx to CUDA.");
        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationNvidiaCuda;

        Assert.True(await WaitForConditionAsync(() => sut.IsAccelerationRestartRequired, TimeSpan.FromSeconds(1)));

        plugin.LoadException = null;
        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationCpu;

        Assert.True(
            await WaitForConditionAsync(() => !sut.IsAccelerationRestartRequired, TimeSpan.FromSeconds(1)),
            "A later acceleration selection that can be loaded in-process should clear the restart prompt.");
    }

    [Fact]
    public void RestartForAccelerationCommand_RequestsMinimizedRestart()
    {
        var settings = new FakeSettingsService(new AppSettings());
        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var restarts = new FakeAppRestartService();
        var sut = new ModelManagerViewModel(
            modelManager,
            settings,
            restarts,
            new FakeNotificationService());

        sut.RestartForAccelerationCommand.Execute(null);

        Assert.Equal(1, restarts.RestartMinimizedCallCount);
    }

    [Fact]
    public async Task SelectedAccelerationOptionValue_AppliesLatestAcceleration_WhenChangesOverlap()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);

        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        await modelManager.LoadModelAsync(fullModelId);
        plugin.BlockNextLoad();

        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationNvidiaCuda;

        Assert.True(
            await plugin.WaitForLoadCountAsync(2, TimeSpan.FromSeconds(1)),
            "The first acceleration change should start reloading the active model.");

        sut.SelectedAccelerationOptionValue = AppSettings.LocalModelAccelerationCpu;
        plugin.ReleaseBlockedLoad();

        Assert.True(
            await plugin.WaitForLoadCountAsync(3, TimeSpan.FromSeconds(2)),
            "A newer overlapping acceleration change should reload again with the latest preference.");
        Assert.True(
            await WaitForConditionAsync(() => !sut.IsBusy, TimeSpan.FromSeconds(1)),
            "The latest acceleration apply should own the final busy state.");
        Assert.Equal(AppSettings.LocalModelAccelerationCpu, settings.Current.LocalModelAcceleration);
        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreferencesAtLoad.Last());
    }

    [Fact]
    public async Task StartRecordingAsync_ReloadsActiveModel_WhenAccelerationChangedOutsideViewModel()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        await modelManager.LoadModelAsync(fullModelId);
        settings.Save(settings.Current with
        {
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda
        });

        var errorLog = new Mock<IErrorLogService>();
        using var audio = new AudioRecordingService(
            new FakeAudioInputDeviceProvider("USB Microphone"),
            new FakeAudioInputCaptureFactory(),
            Timeout.InfiniteTimeSpan);
        using var speechFeedback = new SpeechFeedbackService(
            settings,
            pluginManager,
            new FakeTtsProvider("windows-sapi", "System Voice"));
        var textInsertion = new TextInsertionService(errorLog.Object);
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.Records).Returns([]);
        var workflowTextProcessor = new Mock<IWorkflowTextProcessor>();
        var recentTranscriptions = new RecentTranscriptionsService(
            history.Object,
            new RecentTranscriptionStore(),
            textInsertion,
            settings);
        var workflowPalette = new WorkflowPaletteService(
            _workflows.Object,
            _activeWindow.Object,
            textInsertion,
            settings,
            workflowTextProcessor.Object,
            pluginManager,
            new NoOpWorkflowPalettePresenter());
        var sound = new SoundService { IsEnabled = false };
        using var hotkey = new HotkeyService(settings, _workflows.Object);
        using var sut = new DictationViewModel(
            settings,
            modelManager,
            audio,
            hotkey,
            textInsertion,
            _activeWindow.Object,
            sound,
            history.Object,
            Mock.Of<IDictionaryService>(),
            Mock.Of<IVocabularyBoostingService>(),
            Mock.Of<ISnippetService>(),
            _workflows.Object,
            Mock.Of<ITranslationService>(),
            Mock.Of<IAudioDuckingService>(),
            Mock.Of<IMediaPauseService>(),
            workflowTextProcessor.Object,
            new PostProcessingPipeline(),
            errorLog.Object,
            speechFeedback,
            recentTranscriptions,
            workflowPalette);

        await sut.StartRecordingAsync();

        Assert.Equal(
            [TranscriptionAccelerationPreference.Cpu, TranscriptionAccelerationPreference.NvidiaCuda],
            plugin.AccelerationPreferencesAtLoad);
    }

    [Fact]
    public async Task StartRecordingAsync_StopsQuietly_WhenModelLoadIsCanceled()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId,
            LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu
        });
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true)
        {
            LoadException = new OperationCanceledException()
        };
        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);

        var errorLog = new Mock<IErrorLogService>();
        using var audio = new AudioRecordingService(
            new FakeAudioInputDeviceProvider("USB Microphone"),
            new FakeAudioInputCaptureFactory(),
            Timeout.InfiniteTimeSpan);
        using var speechFeedback = new SpeechFeedbackService(
            settings,
            pluginManager,
            new FakeTtsProvider("windows-sapi", "System Voice"));
        var textInsertion = new TextInsertionService(errorLog.Object);
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.Records).Returns([]);
        var workflowTextProcessor = new Mock<IWorkflowTextProcessor>();
        var recentTranscriptions = new RecentTranscriptionsService(
            history.Object,
            new RecentTranscriptionStore(),
            textInsertion,
            settings);
        var workflowPalette = new WorkflowPaletteService(
            _workflows.Object,
            _activeWindow.Object,
            textInsertion,
            settings,
            workflowTextProcessor.Object,
            pluginManager,
            new NoOpWorkflowPalettePresenter());
        var sound = new SoundService { IsEnabled = false };
        using var hotkey = new HotkeyService(settings, _workflows.Object);
        using var sut = new DictationViewModel(
            settings,
            modelManager,
            audio,
            hotkey,
            textInsertion,
            _activeWindow.Object,
            sound,
            history.Object,
            Mock.Of<IDictionaryService>(),
            Mock.Of<IVocabularyBoostingService>(),
            Mock.Of<ISnippetService>(),
            _workflows.Object,
            Mock.Of<ITranslationService>(),
            Mock.Of<IAudioDuckingService>(),
            Mock.Of<IMediaPauseService>(),
            workflowTextProcessor.Object,
            new PostProcessingPipeline(),
            errorLog.Object,
            speechFeedback,
            recentTranscriptions,
            workflowPalette);

        await sut.StartRecordingAsync();

        Assert.False(sut.IsRecording);
        Assert.False(sut.ShowFeedback);
        Assert.False(sut.FeedbackIsError);
    }

    [Fact]
    public void Constructor_HidesAccelerationControls_ForCloudPluginWithDefaultCpuStatus()
    {
        const string pluginId = "com.typewhisper.openrouter";
        const string modelId = "openai/whisper-1";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(
                pluginId,
                "OpenRouter",
                modelId,
                "OpenAI: Whisper 1",
                configured: true));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.False(sut.IsAccelerationSectionVisible);
        Assert.Equal("", sut.AccelerationStatusText);
    }

    [Fact]
    public void Constructor_ShowsAccelerationControls_ForLocalDownloadPlugin()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(
                pluginId,
                "Parakeet",
                modelId,
                "Parakeet TDT",
                configured: true,
                accelerationStatus: new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.NvidiaCuda,
                    "Using CUDA"),
                supportsModelDownload: true));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.True(sut.IsAccelerationSectionVisible);
        Assert.Equal("Using CUDA", sut.AccelerationStatusText);
    }

    [Fact]
    public void Constructor_ShowsSelectedPluginAccelerationStatus()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(
                pluginId,
                "Parakeet",
                modelId,
                "Parakeet TDT",
                configured: true,
                accelerationStatus: new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.NvidiaCuda,
                    "Using CUDA"),
                supportsModelDownload: true));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.Equal("Using CUDA", sut.AccelerationStatusText);
    }

    [Fact]
    public void Constructor_ExposesConfiguredModelStoragePath()
    {
        var storagePath = Path.Join(Path.GetTempPath(), $"tw-models-{Guid.NewGuid():N}");
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelStoragePath = storagePath
        });

        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.Equal(Path.GetFullPath(storagePath), sut.ModelStoragePath);
        Assert.Equal(Path.GetFullPath(storagePath), sut.ResolvedModelStoragePath);
    }

    [Fact]
    public async Task MoveModelStorageCommand_MigratesDownloadsAndRefreshesPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-models-{Guid.NewGuid():N}");
        var oldRoot = Path.Join(tempDir, "old");
        var newRoot = Path.Join(tempDir, "new");
        var oldModelFile = Path.Join(oldRoot, "translation-en-fr", "config.json");
        Directory.CreateDirectory(Path.Join(oldRoot, "translation-en-fr"));
        await File.WriteAllTextAsync(oldModelFile, "{}");
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelStoragePath = oldRoot
        });

        try
        {
            var pluginManager = CreatePluginManager(settings);
            var modelManager = new ModelManagerService(pluginManager, settings);
            var sut = new ModelManagerViewModel(modelManager, settings);

            sut.ModelStoragePath = newRoot;
            await sut.MoveModelStorageCommand.ExecuteAsync(null);

            Assert.Equal(Path.GetFullPath(newRoot), settings.Current.LocalModelStoragePath);
            Assert.Equal(Path.GetFullPath(newRoot), sut.ResolvedModelStoragePath);
            Assert.True(File.Exists(Path.Join(newRoot, "translation-en-fr", "config.json")));
            Assert.False(File.Exists(oldModelFile));
            Assert.False(sut.IsModelStorageBusy);
            Assert.False(sut.HasModelStorageError);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResetModelStoragePathCommand_ClearsCustomStoragePath()
    {
        var storagePath = Path.Join(Path.GetTempPath(), $"tw-models-{Guid.NewGuid():N}");
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelStoragePath = storagePath
        });

        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        sut.ResetModelStoragePathCommand.Execute(null);

        Assert.Null(settings.Current.LocalModelStoragePath);
        Assert.Equal(LocalModelStorageService.DefaultModelStoragePath, sut.ResolvedModelStoragePath);
    }

    [Fact]
    public void ResetModelStoragePathCommand_IsDisabledWhileStorageMoveIsBusy()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            LocalModelStoragePath = Path.Join(Path.GetTempPath(), $"tw-models-{Guid.NewGuid():N}")
        });

        var pluginManager = CreatePluginManager(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        sut.IsModelStorageBusy = true;

        Assert.False(sut.ResetModelStoragePathCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnloadModel_KeepsSavedSelectionVisible()
    {
        const string pluginId = "com.typewhisper.groq";
        const string modelId = "whisper-large-v3";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(pluginId, "Groq", modelId, "Whisper Large V3", configured: true));
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        await modelManager.LoadModelAsync(fullModelId);
        modelManager.UnloadModel();

        Assert.Equal(fullModelId, sut.SelectedModelOptionId);
        Assert.Equal("Groq", sut.ActiveProviderDisplayName);
        Assert.Equal("Whisper Large V3", sut.ActiveModelDisplayName);
    }

    [Fact]
    public void Constructor_ShowsApiKeyRequiredWithoutBusy_ForUnconfiguredCloudSelection()
    {
        const string pluginId = "com.typewhisper.groq";
        const string modelId = "whisper-large-v3";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(pluginId, "Groq", modelId, "Whisper Large V3", configured: false));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.Equal(fullModelId, sut.SelectedModelOptionId);
        Assert.Equal("Groq", sut.ActiveProviderDisplayName);
        Assert.Equal("Whisper Large V3", sut.ActiveModelDisplayName);
        Assert.Equal(Loc.Instance["Models.StatusApiKeyRequired"], sut.ActiveModelStatusText);
        Assert.False(sut.IsActiveModelReady);
        Assert.False(sut.IsActiveModelBusy);
    }

    [Fact]
    public void RefreshPluginAvailability_MarksConfiguredCloudSelectionReadyWithoutBusy()
    {
        const string pluginId = "com.typewhisper.groq";
        const string modelId = "whisper-large-v3";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });
        var plugin = new FakeTranscriptionPlugin(pluginId, "Groq", modelId, "Whisper Large V3", configured: false);
        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        plugin.IsConfigured = true;
        sut.RefreshPluginAvailability();

        Assert.Equal(Loc.Instance["Models.StatusReady"], sut.ActiveModelStatusText);
        Assert.True(sut.IsActiveModelReady);
        Assert.False(sut.IsActiveModelBusy);
    }

    private PluginManager CreatePluginManager(ISettingsService settings, params ITranscriptionEnginePlugin[] transcriptionEngines)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            settings,
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

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition())
                return true;

            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return condition();
    }

    private sealed class NoOpWorkflowPalettePresenter : IWorkflowPalettePresenter
    {
        public bool IsVisible => false;
        public void Show(WorkflowPaletteViewModel viewModel, Action onClosed) { }
        public void Close() { }
    }

    private sealed class FakeAppRestartService : IAppRestartService
    {
        public int RestartMinimizedCallCount { get; private set; }

        public void RestartMinimized() => RestartMinimizedCallCount++;
    }

    private sealed class FakeNotificationService : IAppNotificationService
    {
        public List<NotificationMessage> Messages { get; } = [];

        public void ShowBalloon(string title, string message, Action? onClick = null) =>
            Messages.Add(new NotificationMessage(title, message, onClick));
    }

    private sealed record NotificationMessage(string Title, string Message, Action? OnClick);

    private sealed class FakeSettingsService(AppSettings initialSettings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initialSettings;
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            string providerDisplayName,
            string modelId,
            string modelDisplayName,
            bool configured,
            TranscriptionAccelerationStatus? accelerationStatus = null,
            bool supportsModelDownload = false,
            IReadOnlyList<TranscriptionAccelerationBackend>? supportedAccelerationBackends = null,
            Func<TranscriptionAccelerationPreference, TranscriptionAccelerationStatus>? accelerationStatusFactory = null)
        {
            PluginId = pluginId;
            ProviderDisplayName = providerDisplayName;
            IsConfigured = configured;
            TranscriptionModels = [new PluginModelInfo(modelId, modelDisplayName)];
            AccelerationStatus = accelerationStatus ?? new TranscriptionAccelerationStatus(
                TranscriptionAccelerationBackend.Cpu,
                "Using CPU");
            SupportsModelDownload = supportsModelDownload;
            SupportedAccelerationBackends = supportedAccelerationBackends ?? [TranscriptionAccelerationBackend.Cpu];
            _accelerationStatusFactory = accelerationStatusFactory;
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName { get; }
        public bool IsConfigured { get; set; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public bool SupportsModelDownload { get; }
        public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends { get; }
        public TranscriptionAccelerationStatus AccelerationStatus { get; private set; }
        public TranscriptionAccelerationPreference LastAccelerationPreference { get; private set; } =
            TranscriptionAccelerationPreference.Auto;
        public int LoadCallCount { get; private set; }
        public Exception? LoadException { get; set; }
        public List<TranscriptionAccelerationPreference> AccelerationPreferencesAtLoad { get; } = [];
        private TaskCompletionSource? _nextLoadBlocker;
        private TaskCompletionSource? _activeLoadBlocker;
        private readonly Func<TranscriptionAccelerationPreference, TranscriptionAccelerationStatus>? _accelerationStatusFactory;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string selectedModelId) => SelectedModelId = selectedModelId;
        public void SetAccelerationPreference(TranscriptionAccelerationPreference preference)
        {
            LastAccelerationPreference = preference;
            if (_accelerationStatusFactory is not null)
                AccelerationStatus = _accelerationStatusFactory(preference);
        }

        public void BlockNextLoad() =>
            _nextLoadBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseBlockedLoad() =>
            (_activeLoadBlocker ?? _nextLoadBlocker)?.TrySetResult();

        public async Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCallCount++;
            AccelerationPreferencesAtLoad.Add(LastAccelerationPreference);
            if (LoadException is not null)
                throw LoadException;

            var blocker = _nextLoadBlocker;
            _nextLoadBlocker = null;
            if (blocker is not null)
            {
                _activeLoadBlocker = blocker;
                try
                {
                    await blocker.Task.WaitAsync(ct);
                }
                finally
                {
                    if (ReferenceEquals(_activeLoadBlocker, blocker))
                        _activeLoadBlocker = null;
                }
            }

            SelectedModelId = modelId;
        }

        public async Task<bool> WaitForLoadCountAsync(int expectedLoadCount, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                if (LoadCallCount >= expectedLoadCount)
                    return true;

                try
                {
                    await Task.Delay(10, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return LoadCallCount >= expectedLoadCount;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));

        public void Dispose() { }
    }
}
