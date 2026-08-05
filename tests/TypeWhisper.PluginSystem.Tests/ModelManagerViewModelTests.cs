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
    public void Constructor_UsesTranscriptionSelectionIdForAdditionalProfileRole()
    {
        const string rootPluginId = "com.typewhisper.openai-compatible";
        const string selectionId = "openai-compatible-profile-a";
        const string modelId = "whisper-profile";
        var fullModelId = ModelManagerService.GetPluginModelId(selectionId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(
                rootPluginId,
                "Local Gateway",
                modelId,
                "Whisper Profile",
                configured: true,
                selectionId: selectionId));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        var provider = Assert.Single(sut.Providers);
        Assert.Equal(selectionId, provider.ProviderId);
        Assert.Equal(fullModelId, Assert.Single(sut.AvailableModelOptions).FullId);
        Assert.Equal(fullModelId, sut.SelectedModelOptionId);
        Assert.Equal("Local Gateway", sut.ActiveProviderDisplayName);
        Assert.Equal("Whisper Profile", sut.ActiveModelDisplayName);
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
    public void LanguageChange_RefreshesLocalizedModelManagerText()
    {
        Loc.Instance.Initialize();
        var previousLanguage = Loc.Instance.CurrentLanguage;

        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var settings = new FakeSettingsService(new AppSettings());
            var pluginManager = CreatePluginManager(settings);
            var modelManager = new ModelManagerService(pluginManager, settings);
            var sut = new ModelManagerViewModel(modelManager, settings);
            var englishNoProvider = sut.ActiveProviderDisplayName;
            var englishNoModel = sut.ActiveModelDisplayName;
            var englishAccelerationAuto = sut.AccelerationOptions.Single(option =>
                option.Value == AppSettings.LocalModelAccelerationAuto).DisplayName;

            Loc.Instance.CurrentLanguage = "zh-Hans";

            Assert.Equal(Loc.Instance["Models.NoProvider"], sut.ActiveProviderDisplayName);
            Assert.Equal(Loc.Instance["Models.NoModelSelected"], sut.ActiveModelDisplayName);
            Assert.Equal(
                Loc.Instance["Models.AccelerationAuto"],
                sut.AccelerationOptions.Single(option =>
                    option.Value == AppSettings.LocalModelAccelerationAuto).DisplayName);
            Assert.Equal(
                Loc.Instance.GetString("Models.StorageCurrentFormat", sut.ResolvedModelStoragePath),
                sut.ModelStorageStatusText);
            Assert.NotEqual(englishNoProvider, sut.ActiveProviderDisplayName);
            Assert.NotEqual(englishNoModel, sut.ActiveModelDisplayName);
            Assert.NotEqual(
                englishAccelerationAuto,
                sut.AccelerationOptions.Single(option =>
                    option.Value == AppSettings.LocalModelAccelerationAuto).DisplayName);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = previousLanguage;
        }
    }

    [Fact]
    public void LanguageChange_RefreshesLocalizedModelStatus()
    {
        Loc.Instance.Initialize();
        var previousLanguage = Loc.Instance.CurrentLanguage;

        try
        {
            Loc.Instance.CurrentLanguage = "en";
            const string pluginId = "com.typewhisper.groq";
            const string modelId = "whisper-large-v3";
            var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
            var settings = new FakeSettingsService(new AppSettings
            {
                SelectedModelId = fullModelId
            });
            var pluginManager = CreatePluginManager(settings,
                new FakeTranscriptionPlugin(
                    pluginId,
                    "Groq",
                    modelId,
                    "Whisper Large V3",
                    configured: false));
            var modelManager = new ModelManagerService(pluginManager, settings);
            var sut = new ModelManagerViewModel(modelManager, settings);
            var englishStatus = sut.ActiveModelStatusText;

            Loc.Instance.CurrentLanguage = "zh-Hans";

            Assert.Equal(Loc.Instance["Models.StatusApiKeyRequired"], sut.ActiveModelStatusText);
            Assert.Equal(
                Loc.Instance["Models.StatusApiKeyRequired"],
                Assert.Single(Assert.Single(sut.Providers).Models).StatusText);
            Assert.NotEqual(englishStatus, sut.ActiveModelStatusText);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = previousLanguage;
        }
    }

    [Fact]
    public async Task LanguageChange_PreservesLocalizedModelLoadErrorWhileBusy()
    {
        Loc.Instance.Initialize();
        var previousLanguage = Loc.Instance.CurrentLanguage;

        try
        {
            Loc.Instance.CurrentLanguage = "en";
            const string pluginId = "com.typewhisper.sherpa-onnx";
            const string modelId = "parakeet";
            const string errorDetail = "Model load failed";
            var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
            var settings = new FakeSettingsService(new AppSettings
            {
                SelectedModelId = fullModelId
            });
            var plugin = new FakeTranscriptionPlugin(
                pluginId,
                "Parakeet",
                modelId,
                "Parakeet TDT",
                configured: true,
                supportsModelDownload: true)
            {
                LoadException = new InvalidOperationException(errorDetail)
            };
            var pluginManager = CreatePluginManager(settings, plugin);
            var modelManager = new ModelManagerService(pluginManager, settings);
            var sut = new ModelManagerViewModel(modelManager, settings);

            var activation = sut.ActivateModelCommand.ExecuteAsync(fullModelId);

            Assert.True(await WaitForConditionAsync(
                () => sut.BusyMessage == Loc.Instance.GetString("Status.ErrorFormat", errorDetail),
                TimeSpan.FromSeconds(1)));

            Loc.Instance.CurrentLanguage = "zh-Hans";

            Assert.True(sut.IsBusy);
            Assert.Equal(Loc.Instance.GetString("Status.ErrorFormat", errorDetail), sut.BusyMessage);
            Assert.NotEqual(Loc.Instance["Models.LoadingModel"], sut.BusyMessage);

            await activation;
        }
        finally
        {
            Loc.Instance.CurrentLanguage = previousLanguage;
        }
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

        plugin.BlockNextLoad();
        var startTask = sut.StartRecordingAsync();

        try
        {
            Assert.True(
                await plugin.WaitForLoadCountAsync(2, TimeSpan.FromSeconds(1)),
                "Starting dictation should begin preparing the changed model.");
            Assert.True(sut.IsRecording);
            Assert.True(audio.IsRecording);
            Assert.True(sut.IsOverlayVisible);
            Assert.Equal(DictationState.Recording, sut.State);
            Assert.False(startTask.IsCompleted);
        }
        finally
        {
            plugin.ReleaseBlockedLoad();
        }

        await startTask;

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
    public async Task StopRecordingAsync_WaitsInProcessingForModelAndTranscribesCompleteAudioOnce()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true)
        {
            ResponseText = ""
        };
        plugin.BlockNextLoad();
        using var fixture = CreateDictationFixture(
            AppSettings.Default with
            {
                SelectedModelId = fullModelId,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu,
                TranscribeShortQuietClipsAggressively = true,
                SaveToHistoryEnabled = false,
                AutoPaste = false
            },
            plugin);

        var startedEvents = new List<RecordingStartedEvent>();
        var stoppedEvents = new List<RecordingStoppedEvent>();
        using var startedSubscription = _eventBus.Subscribe<RecordingStartedEvent>(evt =>
        {
            lock (startedEvents)
                startedEvents.Add(evt);
            return Task.CompletedTask;
        });
        using var stoppedSubscription = _eventBus.Subscribe<RecordingStoppedEvent>(evt =>
        {
            lock (stoppedEvents)
                stoppedEvents.Add(evt);
            return Task.CompletedTask;
        });

        var startTask = fixture.ViewModel.StartRecordingAsync();
        Assert.True(await plugin.WaitForLoadCountAsync(1, TimeSpan.FromSeconds(2)));
        var capture = Assert.Single(fixture.Captures.Created);
        var initialAudio = BuildSpeechPcm16Chunk();
        capture.RaiseData(initialAudio, initialAudio.Length);

        var stopTask = fixture.ViewModel.StopRecordingAsync();
        Assert.True(await WaitForConditionAsync(
            () => !fixture.Audio.IsRecording
                && fixture.ViewModel.State == DictationState.Processing
                && fixture.ViewModel.IsOverlayVisible,
            TimeSpan.FromSeconds(2)));
        Assert.False(stopTask.IsCompleted);

        plugin.ReleaseBlockedLoad();
        await Task.WhenAll(startTask, stopTask);
        Assert.True(await WaitForConditionAsync(
            () => plugin.TranscribeCallCount == 1,
            TimeSpan.FromSeconds(2)));

        Assert.Equal(1, plugin.TranscribeCallCount);
        AssertWavContainsInitialAudio(plugin.LastTranscriptionAudio, initialAudio.Length);
        Assert.Equal(1, capture.StopCount);
        lock (startedEvents)
            Assert.Single(startedEvents);
        lock (stoppedEvents)
            Assert.Single(stoppedEvents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloudBatchProvider_FinalRequestIncludesAudioFromRecordingStart(
        bool liveTranscriptionEnabled)
    {
        const string pluginId = "com.typewhisper.cloud-batch";
        const string modelId = "batch";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Cloud Batch",
            modelId,
            "Batch",
            configured: true)
        {
            ResponseText = ""
        };
        using var fixture = CreateDictationFixture(
            AppSettings.Default with
            {
                SelectedModelId = fullModelId,
                LiveTranscriptionEnabled = liveTranscriptionEnabled,
                TranscribeShortQuietClipsAggressively = true,
                SaveToHistoryEnabled = false,
                AutoPaste = false
            },
            plugin);

        await fixture.ViewModel.StartRecordingAsync();
        var capture = Assert.Single(fixture.Captures.Created);
        var initialAudio = BuildSpeechPcm16Chunk();
        capture.RaiseData(initialAudio, initialAudio.Length);
        if (!liveTranscriptionEnabled)
        {
            var streamingHandler = GetPrivateField(fixture.ViewModel, "_streamingHandler");
            Assert.Equal(0, (int)GetPrivateField(streamingHandler, "_pendingStreamingAudioBytes"));
        }

        await fixture.ViewModel.StopRecordingAsync();
        Assert.True(await WaitForConditionAsync(
            () => plugin.TranscribeCallCount == 1,
            TimeSpan.FromSeconds(2)));

        Assert.Equal(1, plugin.TranscribeCallCount);
        AssertWavContainsInitialAudio(plugin.LastTranscriptionAudio, initialAudio.Length);
        Assert.Equal(1, capture.StopCount);
    }

    [Fact]
    public async Task LateModelContinuationAfterAbort_DoesNotRestartInvalidatedSession()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true)
        {
            ResponseText = ""
        };
        plugin.BlockNextLoadIgnoringCancellation();
        using var fixture = CreateDictationFixture(
            AppSettings.Default with { SelectedModelId = fullModelId },
            plugin);
        var startedEvents = new List<RecordingStartedEvent>();
        var stoppedEvents = new List<RecordingStoppedEvent>();
        var failedEvents = new List<TranscriptionFailedEvent>();
        using var startedSubscription = _eventBus.Subscribe<RecordingStartedEvent>(evt =>
        {
            lock (startedEvents)
                startedEvents.Add(evt);
            return Task.CompletedTask;
        });
        using var stoppedSubscription = _eventBus.Subscribe<RecordingStoppedEvent>(evt =>
        {
            lock (stoppedEvents)
                stoppedEvents.Add(evt);
            return Task.CompletedTask;
        });
        using var failedSubscription = _eventBus.Subscribe<TranscriptionFailedEvent>(evt =>
        {
            lock (failedEvents)
                failedEvents.Add(evt);
            return Task.CompletedTask;
        });

        var startTask = fixture.ViewModel.StartRecordingAsync();
        Assert.True(await plugin.WaitForLoadCountAsync(1, TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Audio.IsRecording);
        var recordingSession = GetPrivateField(fixture.ViewModel, "_activeRecordingSession");
        var preparationCts = Assert.IsType<CancellationTokenSource>(
            recordingSession.GetType()
                .GetProperty("PreparationCts")
                ?.GetValue(recordingSession));

        await fixture.ViewModel.HandleCancelRequested();
        await fixture.ViewModel.HandleCancelRequested();

        Assert.False(fixture.Audio.IsRecording);
        Assert.False(fixture.ViewModel.IsRecording);
        Assert.False(fixture.ViewModel.IsOverlayVisible);

        plugin.ReleaseBlockedLoad();
        await startTask;
        await Task.Delay(100);

        Assert.False(fixture.Audio.IsRecording);
        Assert.Equal(DictationState.Idle, fixture.ViewModel.State);
        Assert.Equal(0, plugin.TranscribeCallCount);
        Assert.Throws<ObjectDisposedException>(() => preparationCts.Cancel());
        lock (startedEvents)
            Assert.Single(startedEvents);
        lock (stoppedEvents)
            Assert.Single(stoppedEvents);
        lock (failedEvents)
            Assert.Single(failedEvents);
    }

    [Fact]
    public async Task Dispose_DefersModelPreparationLockDisposalUntilBlockedLoadReturns()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);
        plugin.BlockNextLoadIgnoringCancellation();
        using var fixture = CreateDictationFixture(
            AppSettings.Default with { SelectedModelId = fullModelId },
            plugin);

        var startTask = fixture.ViewModel.StartRecordingAsync();
        Assert.True(await plugin.WaitForLoadCountAsync(1, TimeSpan.FromSeconds(2)));
        var preparationLock = Assert.IsType<SemaphoreSlim>(
            GetPrivateField(fixture.ViewModel, "_modelPreparationLock"));

        fixture.ViewModel.Dispose();

        Assert.False(preparationLock.Wait(0));
        plugin.ReleaseBlockedLoad();
        await startTask;

        Assert.Throws<ObjectDisposedException>(() => preparationLock.Wait(0));
    }

    [Fact]
    public async Task ApiStartFailure_PreservesMissingModelReason()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);
        using var fixture = CreateDictationFixture(
            AppSettings.Default with { SelectedModelId = "" },
            plugin);

        var sessionId = await fixture.ViewModel.StartRecordingForApiAsync();
        var session = Assert.IsType<ApiDictationSessionSnapshot>(
            fixture.ViewModel.GetApiDictationSession(sessionId));

        Assert.Equal(ApiDictationSessionStatus.Failed, session.Status);
        Assert.Equal(Loc.Instance["Status.NoModelLoaded"], session.Error);
        Assert.False(fixture.Audio.IsRecording);
    }

    [Fact]
    public async Task CancelWhileJobQueueIsFull_DoesNotEnqueueCancelledRecording()
    {
        const string pluginId = "com.typewhisper.cloud-batch";
        const string modelId = "batch";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Cloud Batch",
            modelId,
            "Batch",
            configured: true)
        {
            ResponseText = ""
        };
        plugin.BlockNextTranscription();
        using var fixture = CreateDictationFixture(
            AppSettings.Default with
            {
                SelectedModelId = fullModelId,
                LiveTranscriptionEnabled = false,
                TranscribeShortQuietClipsAggressively = true,
                SaveToHistoryEnabled = false,
                AutoPaste = false
            },
            plugin);

        async Task RecordSpeechAsync()
        {
            await fixture.ViewModel.StartRecordingAsync();
            var capture = fixture.Captures.Created.Last();
            var audio = BuildSpeechPcm16Chunk();
            capture.RaiseData(audio, audio.Length);
            await fixture.ViewModel.StopRecordingAsync();
        }

        await RecordSpeechAsync();
        Assert.True(await WaitForConditionAsync(
            () => plugin.TranscribeCallCount == 1,
            TimeSpan.FromSeconds(2)));

        for (var index = 0; index < 5; index++)
            await RecordSpeechAsync();

        Assert.Equal(6, (int)GetPrivateField(fixture.ViewModel, "_pendingJobCount"));

        await fixture.ViewModel.StartRecordingAsync();
        var blockedCapture = fixture.Captures.Created.Last();
        var blockedAudio = BuildSpeechPcm16Chunk();
        blockedCapture.RaiseData(blockedAudio, blockedAudio.Length);
        var blockedStop = fixture.ViewModel.StopRecordingAsync();
        Assert.True(await WaitForConditionAsync(
            () => !blockedStop.IsCompleted
                && (bool)GetPrivateField(fixture.ViewModel, "_isStoppingRecording"),
            TimeSpan.FromSeconds(2)));

        await fixture.ViewModel.HandleCancelRequested();
        await fixture.ViewModel.HandleCancelRequested();

        Assert.True(await WaitForConditionAsync(
            () => blockedStop.IsCompleted,
            TimeSpan.FromSeconds(2)));
        await blockedStop;
        Assert.Equal(6, (int)GetPrivateField(fixture.ViewModel, "_pendingJobCount"));
        Assert.Equal(1, plugin.TranscribeCallCount);

        fixture.ViewModel.CancelProcessingCommand.Execute(null);
        var queueDrained = await WaitForConditionAsync(
            () => (int)GetPrivateField(fixture.ViewModel, "_pendingJobCount") == 0,
            TimeSpan.FromSeconds(5));

        Assert.True(
            queueDrained,
            $"Expected an empty queue, but observed {GetPrivateField(fixture.ViewModel, "_pendingJobCount")} pending jobs.");
        Assert.Equal(1, plugin.TranscribeCallCount);
    }

    [Fact]
    public async Task DelayedModelFailure_StopsAudioAndPublishesOneFailure()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true)
        {
            LoadExceptionAfterBlock = new InvalidOperationException("Delayed load failed")
        };
        plugin.BlockNextLoad();
        using var fixture = CreateDictationFixture(
            AppSettings.Default with { SelectedModelId = fullModelId },
            plugin);
        var failedEvents = new List<TranscriptionFailedEvent>();
        using var failedSubscription = _eventBus.Subscribe<TranscriptionFailedEvent>(evt =>
        {
            lock (failedEvents)
                failedEvents.Add(evt);
            return Task.CompletedTask;
        });

        var startTask = fixture.ViewModel.StartRecordingAsync();
        Assert.True(await plugin.WaitForLoadCountAsync(1, TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Audio.IsRecording);
        Assert.True(fixture.ViewModel.IsOverlayVisible);

        plugin.ReleaseBlockedLoad();
        await startTask;
        Assert.True(await WaitForConditionAsync(
            () =>
            {
                lock (failedEvents)
                    return failedEvents.Count == 1;
            },
            TimeSpan.FromSeconds(2)));

        Assert.False(fixture.Audio.IsRecording);
        Assert.False(fixture.ViewModel.IsRecording);
        Assert.False(fixture.ViewModel.IsOverlayVisible);
        Assert.True(fixture.ViewModel.ShowFeedback);
        Assert.True(fixture.ViewModel.FeedbackIsError);
        Assert.Equal(0, plugin.TranscribeCallCount);
        Assert.Equal(1, Assert.Single(fixture.Captures.Created).StopCount);
        fixture.History.Verify(h => h.AddRecord(It.IsAny<TranscriptionRecord>()), Times.Never);
        lock (failedEvents)
            Assert.Single(failedEvents);
    }

    [Fact]
    public async Task AlreadyLoadedModel_StartsWithoutReloading()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            "Parakeet",
            modelId,
            "Parakeet TDT",
            configured: true,
            supportsModelDownload: true);
        using var fixture = CreateDictationFixture(
            AppSettings.Default with { SelectedModelId = fullModelId },
            plugin);
        await fixture.ModelManager.LoadModelAsync(fullModelId);

        await fixture.ViewModel.StartRecordingAsync();

        Assert.True(fixture.ViewModel.IsRecording);
        Assert.True(fixture.Audio.IsRecording);
        Assert.True(fixture.ViewModel.IsOverlayVisible);
        Assert.Equal(1, plugin.LoadCallCount);

        await fixture.ViewModel.HandleCancelRequested();
        await fixture.ViewModel.HandleCancelRequested();
        Assert.False(fixture.Audio.IsRecording);
        Assert.Equal(1, Assert.Single(fixture.Captures.Created).StopCount);
    }

    [Fact]
    public async Task DictationCancel_WhileRecording_RequiresConfirmationAndPublishesTerminalPluginEvents()
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
        var startedEvents = new List<RecordingStartedEvent>();
        var stoppedEvents = new List<RecordingStoppedEvent>();
        var failedEvents = new List<TranscriptionFailedEvent>();
        using var startCapture = _eventBus.Subscribe<RecordingStartedEvent>(evt =>
        {
            lock (startedEvents)
                startedEvents.Add(evt);
            return Task.CompletedTask;
        });
        using var stopCapture = _eventBus.Subscribe<RecordingStoppedEvent>(evt =>
        {
            lock (stoppedEvents)
                stoppedEvents.Add(evt);
            return Task.CompletedTask;
        });
        using var failureCapture = _eventBus.Subscribe<TranscriptionFailedEvent>(evt =>
        {
            lock (failedEvents)
                failedEvents.Add(evt);
            return Task.CompletedTask;
        });

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

        Assert.True(await WaitForConditionAsync(
            () =>
            {
                lock (startedEvents)
                    return startedEvents.Count == 1;
            },
            TimeSpan.FromSeconds(2)));
        Guid? recordingId;
        lock (startedEvents)
            recordingId = Assert.Single(startedEvents).RecordingId;

        await sut.HandleCancelRequested();

        Assert.True(sut.IsRecording);
        Assert.Equal(Loc.Instance["Status.CancelRecordingConfirm"], sut.CancelWarningText);
        lock (stoppedEvents)
            Assert.Empty(stoppedEvents);
        lock (failedEvents)
            Assert.Empty(failedEvents);

        await sut.HandleCancelRequested();

        Assert.True(await WaitForConditionAsync(
            () =>
            {
                lock (stoppedEvents)
                    return stoppedEvents.Any(evt => evt.RecordingId == recordingId);
            },
            TimeSpan.FromSeconds(2)));
        Assert.True(await WaitForConditionAsync(
            () =>
            {
                lock (failedEvents)
                    return failedEvents.Any(evt =>
                        evt.RecordingId == recordingId
                        && evt.ErrorMessage == Loc.Instance["Status.Cancelled"]);
            },
            TimeSpan.FromSeconds(2)));
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
    public async Task LanguageChange_ReformatsModelStorageErrorAndPreservesDetail()
    {
        Loc.Instance.Initialize();
        var previousLanguage = Loc.Instance.CurrentLanguage;

        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var settings = new FakeSettingsService(new AppSettings());
            var pluginManager = CreatePluginManager(settings);
            var modelManager = new ModelManagerService(pluginManager, settings);
            var sut = new ModelManagerViewModel(modelManager, settings)
            {
                ModelStoragePath = ""
            };
            var errorDetail = new ArgumentException(
                Loc.Instance["Models.StoragePathRequired"],
                "targetPath").Message;

            await sut.MoveModelStorageCommand.ExecuteAsync(null);

            Assert.True(sut.HasModelStorageError);
            Assert.Equal(
                Loc.Instance.GetString("Models.StorageErrorFormat", errorDetail),
                sut.ModelStorageStatusText);

            Loc.Instance.CurrentLanguage = "zh-Hans";

            Assert.True(sut.HasModelStorageError);
            Assert.Equal(
                Loc.Instance.GetString("Models.StorageErrorFormat", errorDetail),
                sut.ModelStorageStatusText);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = previousLanguage;
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

    private DictationFixture CreateDictationFixture(
        AppSettings initialSettings,
        FakeTranscriptionPlugin plugin)
    {
        var settings = new FakeSettingsService(initialSettings);
        var pluginManager = CreatePluginManager(settings, plugin);
        var modelManager = new ModelManagerService(pluginManager, settings);
        var errorLog = new Mock<IErrorLogService>();
        var captures = new FakeAudioInputCaptureFactory();
        var audio = new AudioRecordingService(
            new FakeAudioInputDeviceProvider("USB Microphone"),
            captures,
            Timeout.InfiniteTimeSpan);
        var speechFeedback = new SpeechFeedbackService(
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
        var hotkey = new HotkeyService(settings, _workflows.Object);
        var viewModel = new DictationViewModel(
            settings,
            modelManager,
            audio,
            hotkey,
            textInsertion,
            _activeWindow.Object,
            new SoundService { IsEnabled = false },
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

        return new DictationFixture(
            viewModel,
            audio,
            captures,
            modelManager,
            pluginManager,
            hotkey,
            speechFeedback,
            history,
            plugin);
    }

    private static byte[] BuildSpeechPcm16Chunk()
    {
        var samples = Enumerable.Repeat((short)8192, 1600).ToArray();
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void AssertWavContainsInitialAudio(byte[]? wavAudio, int expectedInitialBytes)
    {
        Assert.NotNull(wavAudio);
        var dataOffset = -1;
        for (var i = 12; i <= wavAudio.Length - 8; i++)
        {
            if (wavAudio[i] == (byte)'d'
                && wavAudio[i + 1] == (byte)'a'
                && wavAudio[i + 2] == (byte)'t'
                && wavAudio[i + 3] == (byte)'a')
            {
                dataOffset = i + 8;
                break;
            }
        }

        Assert.True(dataOffset > 0, "The final transcription request should contain a WAV data chunk.");
        var dataLength = BitConverter.ToInt32(wavAudio, dataOffset - sizeof(int));
        Assert.True(dataLength >= expectedInitialBytes);
        Assert.Contains(wavAudio.AsSpan(dataOffset, expectedInitialBytes).ToArray(), value => value != 0);
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

    private sealed class DictationFixture(
        DictationViewModel viewModel,
        AudioRecordingService audio,
        FakeAudioInputCaptureFactory captures,
        ModelManagerService modelManager,
        PluginManager pluginManager,
        HotkeyService hotkey,
        SpeechFeedbackService speechFeedback,
        Mock<IHistoryService> history,
        FakeTranscriptionPlugin plugin) : IDisposable
    {
        public DictationViewModel ViewModel { get; } = viewModel;
        public AudioRecordingService Audio { get; } = audio;
        public FakeAudioInputCaptureFactory Captures { get; } = captures;
        public ModelManagerService ModelManager { get; } = modelManager;
        public Mock<IHistoryService> History { get; } = history;

        public void Dispose()
        {
            plugin.ReleaseBlockedLoad();
            plugin.ReleaseBlockedTranscription();
            ViewModel.Dispose();
            hotkey.Dispose();
            speechFeedback.Dispose();
            Audio.Dispose();
            ModelManager.Dispose();
            pluginManager.Dispose();
        }
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        return field.GetValue(target)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was unexpectedly null.");
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

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin, ITranscriptionEngineSelectionIdentity
    {
        private readonly string? _selectionId;

        public FakeTranscriptionPlugin(
            string pluginId,
            string providerDisplayName,
            string modelId,
            string modelDisplayName,
            bool configured,
            TranscriptionAccelerationStatus? accelerationStatus = null,
            bool supportsModelDownload = false,
            IReadOnlyList<TranscriptionAccelerationBackend>? supportedAccelerationBackends = null,
            Func<TranscriptionAccelerationPreference, TranscriptionAccelerationStatus>? accelerationStatusFactory = null,
            string? selectionId = null)
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
            _selectionId = selectionId;
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string TranscriptionSelectionId => _selectionId ?? PluginId;
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
        public Exception? LoadExceptionAfterBlock { get; set; }
        public string ResponseText { get; set; } = "ok";
        public int TranscribeCallCount => Volatile.Read(ref _transcribeCallCount);
        public byte[]? LastTranscriptionAudio { get; private set; }
        public List<TranscriptionAccelerationPreference> AccelerationPreferencesAtLoad { get; } = [];
        private TaskCompletionSource? _nextLoadBlocker;
        private TaskCompletionSource? _activeLoadBlocker;
        private TaskCompletionSource? _nextTranscriptionBlocker;
        private TaskCompletionSource? _activeTranscriptionBlocker;
        private bool _nextLoadIgnoresCancellation;
        private int _transcribeCallCount;
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

        public void BlockNextLoadIgnoringCancellation()
        {
            BlockNextLoad();
            _nextLoadIgnoresCancellation = true;
        }

        public void ReleaseBlockedLoad() =>
            (_activeLoadBlocker ?? _nextLoadBlocker)?.TrySetResult();

        public void BlockNextTranscription() =>
            _nextTranscriptionBlocker = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseBlockedTranscription() =>
            (_activeTranscriptionBlocker ?? _nextTranscriptionBlocker)?.TrySetResult();

        public async Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCallCount++;
            AccelerationPreferencesAtLoad.Add(LastAccelerationPreference);
            if (LoadException is not null)
                throw LoadException;

            var blocker = _nextLoadBlocker;
            var ignoresCancellation = _nextLoadIgnoresCancellation;
            _nextLoadBlocker = null;
            _nextLoadIgnoresCancellation = false;
            if (blocker is not null)
            {
                _activeLoadBlocker = blocker;
                try
                {
                    if (ignoresCancellation)
                        await blocker.Task;
                    else
                        await blocker.Task.WaitAsync(ct);
                }
                finally
                {
                    if (ReferenceEquals(_activeLoadBlocker, blocker))
                        _activeLoadBlocker = null;
                }
            }

            if (LoadExceptionAfterBlock is not null)
                throw LoadExceptionAfterBlock;

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

        public async Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _transcribeCallCount);
            LastTranscriptionAudio = wavAudio;
            var blocker = _nextTranscriptionBlocker;
            _nextTranscriptionBlocker = null;
            if (blocker is not null)
            {
                _activeTranscriptionBlocker = blocker;
                try
                {
                    await blocker.Task.WaitAsync(ct);
                }
                finally
                {
                    if (ReferenceEquals(_activeTranscriptionBlocker, blocker))
                        _activeTranscriptionBlocker = null;
                }
            }

            return new PluginTranscriptionResult(ResponseText, language ?? "en", 1);
        }

        public void Dispose() { }
    }
}
