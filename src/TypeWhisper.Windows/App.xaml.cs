using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.Sync;
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
    private HistoryRetentionCoordinator? _historyRetentionCoordinator;
    private TrayIconService? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private FileTranscriptionWindow? _fileTranscriptionWindow;
    private WelcomeWindow? _welcomeWindow;
    private DispatcherTimer? _protocolCallbackTimer;
    private static readonly string ProtocolCallbackInboxPath = Path.Combine(TypeWhisperEnvironment.DataPath, "protocol-callback.txt");

    /// <summary>
    /// Gets or sets the services value.
    /// </summary>
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Initializes application services, plugin discovery, error handling, and startup windows.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
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
        EnsureCustomProtocolRegistration();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // Load settings
        var settings = _serviceProvider.GetRequiredService<ISettingsService>();
        settings.Load();
        var licenseService = _serviceProvider.GetRequiredService<LicenseService>();

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
        _ = RestoreRemoteTermPacksAsync(termPackRegistry, dictionary, settings, licenseService);

        // Initialize localization
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = settings.Current.UiLanguage
            ?? Loc.Instance.DetectSystemLanguage();

        // Apply staged plugin updates before plugin assemblies are loaded.
        var pluginRegistry = _serviceProvider.GetRequiredService<PluginRegistryService>();
        try
        {
            pluginRegistry.ApplyPendingUpdatesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginRegistry] Failed to apply pending updates at startup: {ex.Message}");
        }

        // Initialize plugins (must happen after settings.Load so enabled state is available)
        var pluginManager = _serviceProvider.GetRequiredService<PluginManager>();
        pluginManager.InitializeAsync().GetAwaiter().GetResult();

        // Validate commercial/supporter licensing state in the background.
        var supporterDiscord = _serviceProvider.GetRequiredService<SupporterDiscordService>();
        _ = Task.Run(async () =>
        {
            await licenseService.ValidateAllIfNeededAsync();
            await supporterDiscord.RefreshStatusIfNeededAsync(licenseService);
        });
        _ = ProcessProtocolArgsAsync(e.Args);
        StartProtocolCallbackWatcher();

        // Plugin registry: first-run auto-install + update check (non-blocking)
        _ = pluginRegistry.FirstRunAutoInstallAsync()
            .ContinueWith(_ => pluginRegistry.CheckForUpdatesAsync(), TaskScheduler.Default)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"Plugin registry check failed: {t.Exception?.Message}");
            });

        // Setup sound service
        var soundService = _serviceProvider.GetRequiredService<SoundService>();
        soundService.IsEnabled = settings.Current.SoundFeedbackEnabled;
        settings.SettingsChanged += s => soundService.IsEnabled = s.SoundFeedbackEnabled;

        // Setup spoken feedback service
        var speechFeedback = _serviceProvider.GetRequiredService<SpeechFeedbackService>();
        speechFeedback.IsEnabled = settings.Current.SpokenFeedbackEnabled;
        settings.SettingsChanged += s => speechFeedback.IsEnabled = s.SpokenFeedbackEnabled;

        _historyRetentionCoordinator = _serviceProvider.GetRequiredService<HistoryRetentionCoordinator>();
        _historyRetentionCoordinator.Initialize();

        // Setup tray icon
        _trayIcon = _serviceProvider.GetRequiredService<TrayIconService>();
        _trayIcon.Initialize();
        _trayIcon.ShowSettingsRequested += (_, _) => RunTrayActionOnUiThread(() => ShowSettingsWindow());
        _trayIcon.ShowFileTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(() => ShowSettingsWindow(SettingsRoute.FileTranscription, presentFileImporter: true));
        _trayIcon.ShowRecentTranscriptionsRequested += (_, _) => RunTrayActionOnUiThread(() =>
            _serviceProvider!.GetRequiredService<DictationViewModel>().ShowRecentTranscriptionsPalette());
        _trayIcon.CopyLastTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(async () =>
            await _serviceProvider!.GetRequiredService<DictationViewModel>().CopyLastTranscriptionToClipboardAsync());
        _trayIcon.ReadBackLastTranscriptionRequested += (_, _) => RunTrayActionOnUiThread(() =>
            _serviceProvider!.GetRequiredService<DictationViewModel>().ReadBackLastTranscription());
        _trayIcon.ToggleRecorderRequested += (_, _) => RunTrayActionOnUiThread(() =>
            _serviceProvider!.GetRequiredService<AudioRecorderViewModel>().ToggleRecordingCommand.Execute(null));
        _trayIcon.ExitRequested += (_, _) => Shutdown();

        // Manual update check from tray menu
        _trayIcon.UpdateCheckRequested += (_, _) => RunTrayActionOnUiThread(async () =>
        {
            var update = _serviceProvider!.GetRequiredService<UpdateService>();
            await update.CheckForUpdatesAsync();
            if (!update.IsUpdateAvailable)
                _trayIcon.ShowBalloon(Loc.Instance["Update.NoUpdate"], Loc.Instance["Update.NoUpdateMessage"]);
        });

        // Create and show overlay window
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Initialize hotkey service (needs window handle)
        var hotkeyService = _serviceProvider.GetRequiredService<HotkeyService>();
        hotkeyService.Initialize(mainWindow);
        hotkeyService.RecorderToggleRequested += (_, _) =>
            Dispatcher.InvokeAsync(() =>
                _serviceProvider.GetRequiredService<AudioRecorderViewModel>().ToggleRecordingCommand.Execute(null));

        // Warm up audio
        var audio = _serviceProvider.GetRequiredService<AudioRecordingService>();
        _ = StartAudioWarmUpInBackground(audio, settings.Current.SelectedMicrophoneDevice);

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

        // Migrate old local model IDs to plugin-prefixed format
        var modelManager = _serviceProvider.GetRequiredService<ModelManagerService>();
        modelManager.MigrateSettings();
        MigrateWorkflowModelOverrides(_serviceProvider);

        // Auto-load previously selected model (after plugin initialization)
        if (!string.IsNullOrEmpty(settings.Current.SelectedModelId))
        {
            if (modelManager.IsDownloaded(settings.Current.SelectedModelId))
            {
                _ = modelManager.LoadModelAsync(settings.Current.SelectedModelId)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            System.Diagnostics.Debug.WriteLine($"Auto-load model failed: {t.Exception?.Message}");
                    });
            }
        }

        if (settings.Current.WatchFolderAutoStart
            && !string.IsNullOrWhiteSpace(settings.Current.WatchFolderPath))
        {
            var fileTranscription = _serviceProvider.GetRequiredService<FileTranscriptionViewModel>();
            fileTranscription.StartWatchFolderFromSettings();
        }

        // Check for updates in background
        var updateService = _serviceProvider.GetRequiredService<UpdateService>();
        updateService.Initialize();
        _ = updateService.CheckForUpdatesAsync();
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
        int? selectedMicrophoneDevice) =>
        Task.Run(() =>
        {
            try
            {
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

        var licenseService = _serviceProvider!.GetRequiredService<LicenseService>();
        var supporterDiscord = _serviceProvider.GetRequiredService<SupporterDiscordService>();
        var handled = await supporterDiscord.HandleCallbackUriAsync(uri, licenseService);
        if (!handled)
            return;

        ShowSettingsWindow(SettingsRoute.License);
    }

    private void ShowSettingsWindow(
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
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _serviceProvider!.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();

        if (_settingsWindow.DataContext is SettingsWindowViewModel viewModel)
            ApplySettingsWindowRequest(viewModel, route, presentFileImporter, focusPluginId);
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
        services.AddSingleton<PluginRegistryService>();
        services.AddSingleton<TermPackRegistryService>();

        // Model manager (plugin-based)
        services.AddSingleton<ModelManagerService>();
        services.AddSingleton(sp => new LocalModelStorageService(
            sp.GetRequiredService<ISettingsService>(),
            () => sp.GetRequiredService<ModelManagerService>().UnloadModel()));
        services.AddSingleton<IFileTranscriptionProcessor, FileTranscriptionProcessor>();

        // Audio
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

        // Post-processing pipeline
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();

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

        // ViewModels
        services.AddSingleton<AudioRecorderViewModel>();
        services.AddSingleton<IRecorderApiController>(sp => sp.GetRequiredService<AudioRecorderViewModel>());
        services.AddSingleton<DictationViewModel>();
        services.AddSingleton<RecordingOverlayViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<DictionaryViewModel>();
        services.AddSingleton<SnippetsViewModel>();
        services.AddSingleton<WorkflowsViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<CloudFolderSyncViewModel>();
        services.AddSingleton<SettingsWindowViewModel>();
        services.AddSingleton<FileTranscriptionViewModel>();
        services.AddSingleton<DashboardViewModel>();
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
        _protocolCallbackTimer?.Stop();
        _historyRetentionCoordinator?.HandleShutdown();
        _trayIcon?.Dispose();
        _serviceProvider?.Dispose();
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
