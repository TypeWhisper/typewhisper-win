using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows;

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

    public static ServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Seed prompt presets
        var promptActions = _serviceProvider.GetRequiredService<IPromptActionService>();
        promptActions.SeedPresets();

        // Load settings
        var settings = _serviceProvider.GetRequiredService<ISettingsService>();
        settings.Load();

        // Restore enabled term packs into the dictionary on startup.
        var dictionary = _serviceProvider.GetRequiredService<IDictionaryService>();
        var enabledPackIds = settings.Current.EnabledPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in TermPack.AllPacks.Where(pack => enabledPackIds.Contains(pack.Id)))
            dictionary.ActivatePack(pack);

        // Initialize localization
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = settings.Current.UiLanguage
            ?? Loc.Instance.DetectSystemLanguage();

        // Initialize plugins (must happen after settings.Load so enabled state is available)
        var pluginManager = _serviceProvider.GetRequiredService<PluginManager>();
        pluginManager.InitializeAsync().GetAwaiter().GetResult();

        // Validate commercial/supporter licensing state in the background.
        var licenseService = _serviceProvider.GetRequiredService<LicenseService>();
        var supporterDiscord = _serviceProvider.GetRequiredService<SupporterDiscordService>();
        _ = Task.Run(async () =>
        {
            await licenseService.ValidateAllIfNeededAsync();
            await supporterDiscord.RefreshStatusIfNeededAsync(licenseService);
        });
        _ = ProcessProtocolArgsAsync(e.Args);
        StartProtocolCallbackWatcher();

        // Plugin registry: first-run auto-install + update check (non-blocking)
        var pluginRegistry = _serviceProvider.GetRequiredService<PluginRegistryService>();
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
        _trayIcon.ShowSettingsRequested += (_, _) => ShowSettingsWindow();
        _trayIcon.ShowFileTranscriptionRequested += (_, _) => ShowSettingsWindow(SettingsRoute.FileTranscription, presentFileImporter: true);
        _trayIcon.ExitRequested += (_, _) => Shutdown();

        // Manual update check from tray menu
        _trayIcon.UpdateCheckRequested += async (_, _) =>
        {
            var update = _serviceProvider!.GetRequiredService<UpdateService>();
            await update.CheckForUpdatesAsync();
            if (!update.IsUpdateAvailable)
                _trayIcon.ShowBalloon(Loc.Instance["Update.NoUpdate"], Loc.Instance["Update.NoUpdateMessage"]);
        };

        // Create and show overlay window
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Initialize hotkey service (needs window handle)
        var hotkeyService = _serviceProvider.GetRequiredService<HotkeyService>();
        hotkeyService.Initialize(mainWindow);

        // Warm up audio
        var audio = _serviceProvider.GetRequiredService<AudioRecordingService>();
        var mic = settings.Current.SelectedMicrophoneDevice;
        if (mic.HasValue) audio.SetMicrophoneDevice(mic);
        if (!audio.WarmUp())
            System.Diagnostics.Debug.WriteLine("No audio input device available at startup. Polling for device...");

        // Start and keep the API server aligned with settings.
        var apiServer = _serviceProvider.GetRequiredService<ApiServerController>();
        apiServer.Initialize();

        // Show onboarding if first run (skip when started minimized)
        if (!settings.Current.HasCompletedOnboarding && !Program.StartMinimized)
        {
            _welcomeWindow = _serviceProvider.GetRequiredService<WelcomeWindow>();
            _welcomeWindow.Closed += (_, _) =>
            {
                settings.Save(settings.Current with { HasCompletedOnboarding = true });
                _welcomeWindow = null;
            };
            _welcomeWindow.Show();
        }

        // Migrate old local model IDs to plugin-prefixed format
        var modelManager = _serviceProvider.GetRequiredService<ModelManagerService>();
        modelManager.MigrateSettings();
        MigrateProfileModelOverrides(_serviceProvider);

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

        // Check for updates in background
        var updateService = _serviceProvider.GetRequiredService<UpdateService>();
        updateService.Initialize();
        _ = updateService.CheckForUpdatesAsync();
    }

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

    private void ShowSettingsWindow(SettingsRoute? route = null, bool presentFileImporter = false)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.DataContext is SettingsWindowViewModel existingViewModel)
            {
                if (presentFileImporter)
                    existingViewModel.OpenFileImporterCommand.Execute(null);
                else if (route.HasValue)
                    existingViewModel.Open(route.Value);
            }
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _serviceProvider!.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();

        if (_settingsWindow.DataContext is SettingsWindowViewModel viewModel)
        {
            if (presentFileImporter)
                viewModel.OpenFileImporterCommand.Execute(null);
            else if (route.HasValue)
                viewModel.Open(route.Value);
        }
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

        // Model manager (plugin-based)
        services.AddSingleton<ModelManagerService>();

        // Audio
        services.AddSingleton<AudioRecordingService>();
        services.AddSingleton<AudioFileService>();
        services.AddSingleton<IAudioDuckingService, AudioDuckingService>();
        services.AddSingleton<IMediaPauseService, MediaPauseService>();

        // Data services (JSON file-based)
        var dataPath = TypeWhisperEnvironment.DataPath;
        services.AddSingleton<IErrorLogService>(
            new ErrorLogService(dataPath));
        services.AddSingleton<IHistoryService>(
            new HistoryService(Path.Combine(dataPath, "history.json"), TypeWhisperEnvironment.AudioPath));
        services.AddSingleton<IDictionaryService>(
            new DictionaryService(Path.Combine(dataPath, "dictionary.json")));
        services.AddSingleton<IVocabularyBoostingService, VocabularyBoostingService>();
        services.AddSingleton<ISnippetService>(
            new SnippetService(Path.Combine(dataPath, "snippets.json")));
        services.AddSingleton<IProfileService>(
            new ProfileService(Path.Combine(dataPath, "profiles.json")));
        services.AddSingleton<IPromptActionService>(
            new PromptActionService(Path.Combine(dataPath, "prompt-actions.json")));

        // Post-processing pipeline
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();

        // Translation (uses plugin manager for LLM providers)
        services.AddSingleton<ITranslationService>(sp =>
            new TranslationService(sp.GetRequiredService<PluginManager>()));

        // Services
        services.AddSingleton<SpeechFeedbackService>();
        services.AddSingleton<HistoryRetentionCoordinator>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TextInsertionService>();
        services.AddSingleton<IActiveWindowService, ActiveWindowService>();
        services.AddSingleton<SoundService>();
        services.AddSingleton<HttpApiService>();
        services.AddSingleton<ILocalApiServer>(sp => sp.GetRequiredService<HttpApiService>());
        services.AddSingleton<ApiServerController>();
        services.AddSingleton<CliInstallService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<PromptProcessingService>();

        // License
        services.AddSingleton<LicenseService>();
        services.AddSingleton<SupporterDiscordService>();

        // ViewModels
        services.AddSingleton<AudioRecorderViewModel>();
        services.AddSingleton<DictationViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<DictionaryViewModel>();
        services.AddSingleton<SnippetsViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<PromptsViewModel>();
        services.AddSingleton<PromptPaletteViewModel>();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _protocolCallbackTimer?.Stop();
        _historyRetentionCoordinator?.HandleShutdown();
        _trayIcon?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void MigrateProfileModelOverrides(ServiceProvider sp)
    {
        try
        {
            var profileService = sp.GetRequiredService<IProfileService>();
            foreach (var profile in profileService.Profiles)
            {
                var migrated = ModelManagerService.MigrateModelId(profile.TranscriptionModelOverride);
                if (migrated != profile.TranscriptionModelOverride)
                    profileService.UpdateProfile(profile with { TranscriptionModelOverride = migrated });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Profile model migration failed: {ex.Message}");
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
