using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.SpokenFormatting;
using TypeWhisper.Core.Services.Sync;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows;

/// <summary>
/// Provides app behavior.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private readonly CancellationTokenSource _startupCancellation = new();
    private Task? _startupBackgroundTask;
    private HistoryRetentionCoordinator? _historyRetentionCoordinator;
    private TrayIconService? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private FileTranscriptionWindow? _fileTranscriptionWindow;
    private WelcomeWindow? _welcomeWindow;
    private DispatcherTimer? _protocolCallbackTimer;
    private RegisteredWaitHandle? _singleInstanceActivationRegistration;
    private bool _startupPresentationReady;
    private bool _pendingSingleInstanceActivation;
    private static string ProtocolCallbackInboxPath => Path.Combine(TypeWhisperEnvironment.DataPath, "protocol-callback.txt");

    /// <summary>
    /// Gets or sets the services value.
    /// </summary>
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Initializes application services, plugin discovery, error handling, and startup windows.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AudioCaptureDiagnostics.Reset();

        DispatcherUnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled UI exception: {args.Exception}");
            LogCrash(args.Exception);
            MessageBox.Show(Loc.Instance.GetString("App.ErrorFormat", args.Exception.Message),
                Loc.Instance["App.ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Unhandled domain exception: {ex}");
                LogCrash(ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unobserved task exception: {args.Exception}");
            LogCrash(args.Exception);
            args.SetObserved();
        };

        TypeWhisperEnvironment.EnsureDirectories();
        if (!Program.UiAutomation.IsEnabled)
            EnsureCustomProtocolRegistration();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // Load settings
        var settings = _serviceProvider.GetRequiredService<ISettingsService>();
        settings.Load();
        if (Program.UiAutomation.IsEnabled)
        {
            _serviceProvider.GetRequiredService<DevelopmentDataSeeder>().ClearAndSeed(
                Program.UiAutomation.ReferenceUtc);
            settings.Save(settings.Current with
            {
                UiLanguage = Program.UiAutomation.Language,
                HasCompletedOnboarding = true,
                PluginFirstRunCompleted = true
            });
        }

        var recoveryStore = _serviceProvider.GetRequiredService<DictationRecoveryAudioStore>();
        await recoveryStore.InitializeAsync();
        await recoveryStore.SetRetentionAsync(settings.Current.DictationRecoveryRetentionDays);
        settings.SettingsChanged += updated =>
            _ = recoveryStore.SetRetentionAsync(updated.DictationRecoveryRetentionDays);
        var licenseService = _serviceProvider.GetRequiredService<LicenseService>();
        if (Program.UiAutomation.HasPremiumFixture)
        {
            licenseService.CommercialStatus = LicenseStatus.Active;
            licenseService.CommercialTier = CommercialLicenseTier.Team;
        }

        // Restore enabled term packs into the dictionary on startup.
        var dictionary = _serviceProvider.GetRequiredService<IDictionaryService>();
        var enabledPackIds = settings.Current.EnabledPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!licenseService.HasCommercialLicense)
        {
            foreach (var packId in enabledPackIds.Where(TermPack.IndustryPackIds.Contains).ToArray())
                dictionary.DeactivatePack(packId);

            enabledPackIds.RemoveWhere(TermPack.IndustryPackIds.Contains);
            settings.Save(settings.Current with { EnabledPackIds = enabledPackIds.ToArray() });
        }
        foreach (var pack in TermPack.VisiblePacks(licenseService.HasCommercialLicense).Where(pack => enabledPackIds.Contains(pack.Id)))
            dictionary.ActivatePack(pack);
        var termPackRegistry = _serviceProvider.GetRequiredService<TermPackRegistryService>();
        if (!Program.UiAutomation.IsEnabled)
            _ = RestoreRemoteTermPacksAsync(termPackRegistry, dictionary, settings, licenseService);

        // Initialize localization
        Loc.Instance.CurrentLanguage = settings.Current.UiLanguage
            ?? Loc.Instance.DetectSystemLanguage();

        // Configure feedback before the overlay graph is created.
        var soundService = _serviceProvider.GetRequiredService<SoundService>();
        soundService.IsEnabled = settings.Current.SoundFeedbackEnabled;
        settings.SettingsChanged += s => soundService.IsEnabled = s.SoundFeedbackEnabled;

        var speechFeedback = _serviceProvider.GetRequiredService<SpeechFeedbackService>();
        speechFeedback.IsEnabled = settings.Current.SpokenFeedbackEnabled;
        settings.SettingsChanged += s => speechFeedback.IsEnabled = s.SpokenFeedbackEnabled;

        if (Program.UiAutomation.IsEnabled)
        {
            await StartUiAutomationSessionAsync();
            return;
        }

        // Publish the native shell before plugin discovery. The tray is the
        // primary idle surface, while the transparent overlay creates the
        // window handle needed by hotkeys later in startup.
        _trayIcon = _serviceProvider.GetRequiredService<TrayIconService>();
        _trayIcon.Initialize();
        _trayIcon.ShowSettingsRequested += (_, _) => RunTrayActionOnUiThread(() => ShowSettingsWindow());
        _trayIcon.ShowFileTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(() => ShowSettingsWindow(SettingsRoute.FileTranscription, presentFileImporter: true));
        _trayIcon.RecoverLastRecordingRequested += (_, _) => RunTrayActionOnUiThread(() =>
        {
            _serviceProvider!.GetRequiredService<RecoveryViewModel>().SelectRecording();
            ShowSettingsWindow(SettingsRoute.Recovery);
        });
        _trayIcon.ShowRecentTranscriptionsRequested += (_, _) => RunTrayActionOnUiThread(() =>
            _serviceProvider!.GetRequiredService<DictationViewModel>().ShowRecentTranscriptionsPalette());
        _trayIcon.CopyLastTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(async () =>
            await _serviceProvider!.GetRequiredService<DictationViewModel>().CopyLastTranscriptionToClipboardAsync());
        _trayIcon.ReadBackLastTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(() =>
            _serviceProvider!.GetRequiredService<DictationViewModel>().ReadBackLastTranscription());
        _trayIcon.ToggleRecorderRequested += (_, _) => RunTrayActionOnUiThread(() =>
            _serviceProvider!.GetRequiredService<AudioRecorderViewModel>().ToggleRecordingCommand.Execute(null));
        _trayIcon.ExitRequested += (_, _) => Shutdown();
        _trayIcon.UpdateCheckRequested += (_, _) => RunTrayActionOnUiThread(async () =>
        {
            var update = _serviceProvider!.GetRequiredService<UpdateService>();
            await update.CheckForUpdatesAsync();
            if (!update.IsUpdateAvailable)
                _trayIcon.ShowBalloon(Loc.Instance["Update.NoUpdate"], Loc.Instance["Update.NoUpdateMessage"]);
        });

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
        StartSingleInstanceActivationListener();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        // Apply staged plugin updates before plugin assemblies are loaded.
        var pluginRegistry = _serviceProvider.GetRequiredService<PluginRegistryService>();
        try
        {
            await RunTrackedStartupWorkAsync(
                token => pluginRegistry.ApplyPendingUpdatesAsync(token));
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsNonFatalStartupException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[PluginRegistry] Failed to apply pending updates at startup: {ex.Message}");
            LogCrash(ex);
        }

        if (_startupCancellation.IsCancellationRequested
            || Dispatcher.HasShutdownStarted
            || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Initialize plugins (must happen after settings.Load so enabled state is available)
        var pluginManager = _serviceProvider.GetRequiredService<PluginManager>();
        try
        {
            await RunTrackedStartupWorkAsync(
                token => pluginManager.InitializeAsync(token));
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsNonFatalStartupException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"[PluginManager] Failed to initialize plugins at startup: {ex.Message}");
            LogCrash(ex);
        }

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        // Validate commercial/supporter licensing state in the background.
        var supporterDiscord = _serviceProvider.GetRequiredService<SupporterDiscordService>();
        _ = Task.Run(async () =>
        {
            await licenseService.ValidateAllIfNeededAsync();
            await supporterDiscord.RefreshStatusIfNeededAsync(licenseService);
        });
        _ = ProcessProtocolArgsAsync(e.Args);
        StartProtocolCallbackWatcher();

        // Plugin registry: mark first-run plugin setup complete without installing marketplace plugins.
        _ = pluginRegistry.FirstRunAutoInstallAsync()
            .ContinueWith(_ => pluginRegistry.CheckForUpdatesAsync(), TaskScheduler.Default)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"Plugin registry check failed: {t.Exception?.Message}");
            });

        // Run the one-time history backfill before retention can remove source records.
        _serviceProvider.GetRequiredService<IUsageStatisticsService>();
        _historyRetentionCoordinator = _serviceProvider.GetRequiredService<HistoryRetentionCoordinator>();
        _historyRetentionCoordinator.Initialize();

        // Initialize hotkey service (needs window handle)
        var hotkeyService = _serviceProvider.GetRequiredService<HotkeyService>();
        hotkeyService.Initialize(mainWindow);
        hotkeyService.RecorderToggleRequested += (_, _) =>
            Dispatcher.InvokeAsync(() =>
                _serviceProvider.GetRequiredService<AudioRecorderViewModel>().ToggleRecordingCommand.Execute(null));

        // Warm up audio in the background so startup stays responsive.
        var audio = _serviceProvider.GetRequiredService<AudioRecordingService>();
        _ = StartAudioWarmUpInBackground(
            audio,
            settings.Current.MicrophonePriorityList,
            settings.Current.SelectedMicrophoneDevice);

        // Start and keep the API server aligned with settings.
        var apiServer = _serviceProvider.GetRequiredService<ApiServerController>();
        apiServer.Initialize();

        // Show onboarding if first run (skip when started minimized)
        if (!settings.Current.HasCompletedOnboarding && !Program.StartMinimized)
        {
            _welcomeWindow = _serviceProvider.GetRequiredService<WelcomeWindow>();
            _welcomeWindow.Closed += (sender, _) =>
            {
                var completionRequest = (sender as WelcomeWindow)?.DataContext is WelcomeViewModel viewModel
                    ? viewModel.CompletionRequest
                    : WelcomeCompletionRequest.None;
                settings.Save(settings.Current with { HasCompletedOnboarding = true });
                _welcomeWindow = null;
                if (completionRequest.SettingsRoute is { } route)
                    ShowSettingsWindow(route, focusPluginId: completionRequest.PluginIdToConfigure);
            };
            _welcomeWindow.Show();
        }

        _startupPresentationReady = true;
        if (_pendingSingleInstanceActivation)
        {
            _pendingSingleInstanceActivation = false;
            ActivatePrimaryInstance();
        }

        // Migrate old local model IDs to plugin-prefixed format
        var modelManager = _serviceProvider.GetRequiredService<ModelManagerService>();
        modelManager.MigrateSettings();
        MigrateWorkflowModelOverrides(_serviceProvider);

        if (settings.Current.WatchFolderAutoStart
            && !string.IsNullOrWhiteSpace(settings.Current.WatchFolderPath))
        {
            var fileTranscription = _serviceProvider.GetRequiredService<FileTranscriptionViewModel>();
            fileTranscription.StartWatchFolderFromSettings();
        }

        // Check for updates in background
        var updateService = _serviceProvider.GetRequiredService<UpdateService>();
        updateService.Initialize();
        if (updateService.ShouldCheckAutomatically)
            _ = updateService.CheckForUpdatesAsync();
    }

    private async Task RunTrackedStartupWorkAsync(Func<CancellationToken, Task> work)
    {
        var token = _startupCancellation.Token;
        var task = Task.Run(() => work(token), token);
        Volatile.Write(ref _startupBackgroundTask, task);
        try
        {
            await task;
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _startupBackgroundTask, null, task);
        }
    }

    private async Task StartUiAutomationSessionAsync()
    {
        var pluginManager = _serviceProvider!.GetRequiredService<PluginManager>();
        await pluginManager.InitializeAsync(_startupCancellation.Token);

        ShowSettingsWindow(SettingsRoute.Dashboard);
        _startupPresentationReady = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var readyFile = Program.UiAutomation.ReadyFile;
        if (string.IsNullOrWhiteSpace(readyFile))
            return;

        var directory = Path.GetDirectoryName(readyFile);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var ready = System.Text.Json.JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId,
            AutomationId = "SettingsWindow",
            Language = Program.UiAutomation.Language,
            Premium = Program.UiAutomation.HasPremiumFixture
        });
        await File.WriteAllTextAsync(readyFile, ready, _startupCancellation.Token);
    }

    private void RunTrayActionOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.InvokeAsync(action);
    }

    private void RunTrayActionOnUiThread(Func<Task> action)
    {
        if (Dispatcher.CheckAccess())
        {
            _ = RunTrayActionAsync(action);
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            _ = RunTrayActionAsync(action);
        });
    }

    private static async Task RunTrayActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (IsNonFatalTrayActionException(ex))
        {
            System.Diagnostics.Debug.WriteLine($"Tray action failed: {ex}");
            LogCrash(ex);
        }
    }

    private static bool IsNonFatalTrayActionException(Exception ex) =>
        NonFatalExceptionFilter.IsNonFatal(ex);

    internal static Task StartAudioWarmUpInBackground(
        AudioRecordingService audio,
        IReadOnlyList<MicrophonePriorityItem> microphonePriorityList,
        int? selectedMicrophoneDevice) =>
        Task.Run(() =>
        {
            try
            {
                audio.SetMicrophonePriorityList(microphonePriorityList);

                if (selectedMicrophoneDevice.HasValue)
                    audio.SetMicrophoneDevice(selectedMicrophoneDevice);

                if (!audio.WarmUp())
                    System.Diagnostics.Debug.WriteLine("No audio input device available at startup. Polling for device...");
            }
            catch (Exception ex) when (IsNonFatalStartupException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"Audio warm-up failed: {ex.Message}");
            }
        });

    private static bool IsNonFatalStartupException(Exception ex) =>
        NonFatalExceptionFilter.IsNonFatal(ex);

    private async Task ProcessProtocolArgsAsync(string[] args)
    {
        var raw = args.FirstOrDefault(SupporterDiscordService.CanHandleCallbackUri);
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return;

        await HandleProtocolCallbackUriAsync(uri);
    }

    private void StartProtocolCallbackWatcher()
    {
        _protocolCallbackTimer?.Stop();
        _protocolCallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _protocolCallbackTimer.Tick += async (_, _) =>
        {
            try
            {
                if (!File.Exists(ProtocolCallbackInboxPath))
                    return;

                var raw = File.ReadAllText(ProtocolCallbackInboxPath).Trim();
                File.Delete(ProtocolCallbackInboxPath);

                if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    await HandleProtocolCallbackUriAsync(uri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Protocol callback watcher failed: {ex.Message}");
            }
        };
        _protocolCallbackTimer.Start();
    }

    private async Task HandleProtocolCallbackUriAsync(Uri uri)
    {
        if (!SupporterDiscordService.CanHandleCallbackUri(uri))
            return;

        var serviceProvider = Volatile.Read(ref _serviceProvider);
        if (serviceProvider is null)
            return;

        var licenseService = serviceProvider.GetRequiredService<LicenseService>();
        var supporterDiscord = serviceProvider.GetRequiredService<SupporterDiscordService>();
        var handled = await supporterDiscord.HandleCallbackUriAsync(uri, licenseService);
        if (!handled)
            return;

        ShowSettingsWindow(SettingsRoute.License);
    }

    private void StartSingleInstanceActivationListener()
    {
        _singleInstanceActivationRegistration = Program.ListenForActivationRequests(() =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_startupPresentationReady)
                {
                    _pendingSingleInstanceActivation = true;
                    return;
                }

                ActivatePrimaryInstance();
            });
        });
    }

    private void ActivatePrimaryInstance()
    {
        if (_welcomeWindow is { IsLoaded: true })
        {
            RestoreAndActivateWindow(_welcomeWindow);
            return;
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            RestoreAndActivateWindow(_settingsWindow);
            return;
        }

        ShowSettingsWindow(SettingsRoute.Dashboard);
    }

    internal void ShowSettingsWindow(
        SettingsRoute? route = null,
        bool presentFileImporter = false,
        string? focusPluginId = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowSettingsWindow(route, presentFileImporter, focusPluginId));
            return;
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.DataContext is SettingsWindowViewModel existingViewModel)
                ApplySettingsWindowRequest(existingViewModel, route, presentFileImporter, focusPluginId);
            RestoreAndActivateWindow(_settingsWindow);
            return;
        }

        _settingsWindow = _serviceProvider!.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (Program.UiAutomation.IsEnabled)
                Shutdown();
        };
        _settingsWindow.Show();

        if (_settingsWindow.DataContext is SettingsWindowViewModel viewModel)
            ApplySettingsWindowRequest(viewModel, route, presentFileImporter, focusPluginId);

        RestoreAndActivateWindow(_settingsWindow);
    }

    private static void RestoreAndActivateWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        if (!window.IsVisible)
            window.Show();

        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(handle);
    }

    private static void ApplySettingsWindowRequest(
        SettingsWindowViewModel viewModel,
        SettingsRoute? route,
        bool presentFileImporter,
        string? focusPluginId)
    {
        if (presentFileImporter)
            viewModel.OpenFileImporterCommand.Execute(null);
        else if (route.HasValue)
            viewModel.Open(route.Value);

        if (!string.IsNullOrWhiteSpace(focusPluginId))
            viewModel.FocusInstalledPlugin(focusPluginId);
    }

    private void ShowFileTranscriptionWindow()
    {
        if (_fileTranscriptionWindow is { IsLoaded: true })
        {
            _fileTranscriptionWindow.Activate();
            return;
        }

        _fileTranscriptionWindow = _serviceProvider!.GetRequiredService<FileTranscriptionWindow>();
        _fileTranscriptionWindow.Closed += (_, _) => _fileTranscriptionWindow = null;
        _fileTranscriptionWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core
        services.AddSingleton<ISettingsService>(
            new SettingsService(TypeWhisperEnvironment.SettingsFilePath));

        // Plugin infrastructure
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginEventBus>();
        services.AddSingleton<PluginManager>();
        if (Program.UiAutomation.IsEnabled)
        {
            services.AddSingleton(sp =>
            {
                var registryFile = Program.UiAutomation.PluginRegistryFile;
                var httpClient = string.IsNullOrWhiteSpace(registryFile)
                    ? new HttpClient()
                    : new HttpClient(new UiAutomationRegistryMessageHandler(registryFile));
                return new PluginRegistryService(
                    sp.GetRequiredService<PluginManager>(),
                    sp.GetRequiredService<PluginLoader>(),
                    sp.GetRequiredService<ISettingsService>(),
                    httpClient: httpClient,
                    officialRegistryUrl: string.IsNullOrWhiteSpace(registryFile)
                        ? string.Empty
                        : "https://ui-automation.typewhisper.invalid/plugins.json",
                    communityRegistryUrl: string.Empty);
            });
        }
        else
        {
            services.AddSingleton<PluginRegistryService>();
        }
        services.AddSingleton<TermPackRegistryService>();

        // Model manager (plugin-based)
        services.AddSingleton<ModelManagerService>();
        services.AddSingleton(sp => new LocalModelStorageService(
            sp.GetRequiredService<ISettingsService>(),
            () => sp.GetRequiredService<ModelManagerService>().UnloadModel()));
        services.AddSingleton<IFileTranscriptionProcessor, FileTranscriptionProcessor>();

        // Audio
        services.AddSingleton<DictationRecoveryAudioStore>();
        services.AddSingleton<AudioRecordingService>();
        services.AddSingleton<SystemAudioCaptureService>();
        services.AddSingleton<RecorderCaptureService>();
        services.AddSingleton<AudioFileService>();
        services.AddSingleton<IAudioDuckingService, AudioDuckingService>();
        services.AddSingleton<IMediaPauseService, MediaPauseService>();

        // Data services (JSON file-based)
        var dataPath = TypeWhisperEnvironment.DataPath;
        services.AddSingleton<IErrorLogService>(
            new ErrorLogService(dataPath));
        services.AddSingleton<IHistoryService>(
            new HistoryService(Path.Combine(dataPath, "history.json"), TypeWhisperEnvironment.AudioPath));
        services.AddSingleton<IUsageStatisticsService>(sp =>
        {
            var statistics = new UsageStatisticsService(Path.Combine(dataPath, "usage-statistics.json"));
            statistics.BackfillFromHistoryIfNeeded(sp.GetRequiredService<IHistoryService>().Records);
            return statistics;
        });
        services.AddSingleton<RecentTranscriptionStore>();
        services.AddSingleton<IDictionaryService>(
            new DictionaryService(Path.Combine(dataPath, "dictionary.json")));
        services.AddSingleton<IVocabularyBoostingService, VocabularyBoostingService>();
        services.AddSingleton<ISnippetService>(
            new SnippetService(Path.Combine(dataPath, "snippets.json")));
        services.AddSingleton<IUserDataSyncStore>(sp => new TypeWhisperUserDataSyncStore(
            sp.GetRequiredService<IDictionaryService>(),
            sp.GetRequiredService<ISnippetService>()));
        services.AddSingleton<IWorkflowService>(
            new WorkflowService(Path.Combine(dataPath, "workflows.json")));
        services.AddSingleton<IBackupPluginHandler, BackupPluginHandler>();
        services.AddSingleton<IBackupRestoreService, BackupRestoreService>();
        services.AddSingleton<DevelopmentDataSeeder>();

        // Post-processing pipeline
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();
        services.AddSingleton<SpokenFormattingRulesLoader>();
        services.AddSingleton<SpokenFormattingService>();
        services.AddSingleton<SpokenFormattingProfileStore>();
        services.AddSingleton<SpokenFormattingStrategyResolver>();

        // Translation (uses plugin manager for LLM providers)
        services.AddSingleton<ITranslationService>(sp =>
            new TranslationService(
                sp.GetRequiredService<PluginManager>(),
                sp.GetRequiredService<ISettingsService>()));

        // Services
        services.AddSingleton<SpeechFeedbackService>();
        services.AddSingleton<HistoryRetentionCoordinator>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TextInsertionService>();
        services.AddSingleton<ITargetAppTextObserver, TargetAppTextObservationService>();
        services.AddSingleton<ITargetAppCorrectionCommitObserver, TargetAppCorrectionCommitObserver>();
        services.AddSingleton<TargetAppCorrectionLearningService>();
        services.AddSingleton<RecentTranscriptionsService>();
        services.AddSingleton<WorkflowPaletteService>();
        services.AddSingleton<IActiveWindowService, ActiveWindowService>();
        services.AddSingleton<WindowsAppDiscoveryService>();
        services.AddSingleton<SoundService>();
        services.AddSingleton<HttpApiService>();
        services.AddSingleton<ILocalApiServer>(sp => sp.GetRequiredService<HttpApiService>());
        services.AddSingleton<ApiServerController>();
        services.AddSingleton<CliInstallService>();
        services.AddSingleton<WatchFolderService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<IAppNotificationService>(sp => sp.GetRequiredService<TrayIconService>());
        services.AddSingleton<IAppRestartService, AppRestartService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<PromptProcessingService>();
        services.AddSingleton<IWorkflowTextProcessor>(sp => sp.GetRequiredService<PromptProcessingService>());

        // License
        services.AddSingleton<LicenseService>();
        services.AddSingleton<SupporterDiscordService>();
        services.AddSingleton<AutomaticTranscriptionFallbackService>();
        services.AddSingleton<WorkflowPostProcessingService>();
        services.AddSingleton<IWorkflowPostProcessingService>(sp => sp.GetRequiredService<WorkflowPostProcessingService>());
        services.AddSingleton<HistoryWorkflowRetryService>();
        services.AddSingleton<ManualAudioRecoveryService>();

        // ViewModels
        services.AddSingleton<AudioRecorderViewModel>();
        services.AddSingleton<IRecorderApiController>(sp => sp.GetRequiredService<AudioRecorderViewModel>());
        services.AddSingleton<DictationViewModel>();
        services.AddSingleton<IDictationApiController>(sp => sp.GetRequiredService<DictationViewModel>());
        services.AddSingleton<RecordingOverlayViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SpokenFormattingViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<DictionaryTrainingViewModel>();
        services.AddSingleton<DictionaryViewModel>();
        services.AddSingleton<SnippetsViewModel>();
        services.AddSingleton<WorkflowsViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<CloudFolderSyncViewModel>();
        services.AddSingleton<SettingsWindowViewModel>();
        services.AddSingleton<FileTranscriptionViewModel>();
        services.AddSingleton<RecoveryViewModel>();
        services.AddTransient<WelcomeViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<FileTranscriptionWindow>();
        services.AddTransient<WelcomeWindow>();
    }

    private static void EnsureCustomProtocolRegistration()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return;

            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\typewhisper");
            if (key is null)
                return;

            key.SetValue(string.Empty, "URL:TypeWhisper Protocol");
            key.SetValue("URL Protocol", string.Empty);

            using var iconKey = key.CreateSubKey("DefaultIcon");
            iconKey?.SetValue(string.Empty, $"\"{exePath}\",0");

            using var commandKey = key.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Protocol registration failed: {ex.Message}");
        }
    }

    private static async Task RestoreRemoteTermPacksAsync(
        TermPackRegistryService termPackRegistry,
        IDictionaryService dictionary,
        ISettingsService settings,
        LicenseService licenseService)
    {
        if (!licenseService.HasCommercialLicense || settings.Current.EnabledPackIds.Length == 0)
            return;

        var enabledPackIds = settings.Current.EnabledPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remotePacks = await termPackRegistry.GetRemotePacksAsync();
        foreach (var pack in remotePacks.Where(pack =>
            enabledPackIds.Contains(pack.Id)
            && (licenseService.HasCommercialLicense || !pack.RequiresCommercialLicense)))
        {
            dictionary.ActivatePack(pack);
        }
    }

    /// <summary>
    /// Stops background services, persists shutdown-sensitive state, and releases application resources.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _startupCancellation.Cancel();
        _singleInstanceActivationRegistration?.Unregister(null);
        _singleInstanceActivationRegistration = null;
        _protocolCallbackTimer?.Stop();
        _historyRetentionCoordinator?.HandleShutdown();
        _trayIcon?.Dispose();

        var serviceProvider = Interlocked.Exchange(ref _serviceProvider, null);
        Services = null!;
        var startupTask = Volatile.Read(ref _startupBackgroundTask);
        if (serviceProvider is not null)
        {
            if (startupTask is { IsCompleted: false })
            {
                _ = startupTask.ContinueWith(
                    static (_, state) => ((ServiceProvider)state!).Dispose(),
                    serviceProvider,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                serviceProvider.Dispose();
            }
        }

        base.OnExit(e);
    }

    private static void MigrateWorkflowModelOverrides(ServiceProvider sp)
    {
        try
        {
            var workflowService = sp.GetRequiredService<IWorkflowService>();
            foreach (var workflow in workflowService.Workflows)
            {
                var migrated = ModelManagerService.MigrateModelId(workflow.Behavior.TranscriptionModelOverride);
                if (migrated != workflow.Behavior.TranscriptionModelOverride)
                {
                    workflowService.UpdateWorkflow(workflow with
                    {
                        Behavior = workflow.Behavior with { TranscriptionModelOverride = migrated }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Workflow model migration failed: {ex.Message}");
        }
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            // Structured error log (if DI is ready)
            if (Services?.GetService<IErrorLogService>() is { } errorLog)
                errorLog.AddEntry(ex.Message, ErrorCategory.General);

            // Also keep crash.log as safety net
            var logPath = System.IO.Path.Combine(TypeWhisperEnvironment.LogsPath, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n";
            System.IO.File.AppendAllText(logPath, entry);
        }
        catch { /* ignore logging failures */ }
    }
}
