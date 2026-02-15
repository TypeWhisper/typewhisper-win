using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayIconService? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private FileTranscriptionWindow? _fileTranscriptionWindow;
    private WelcomeWindow? _welcomeWindow;

    public static ServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled UI exception: {args.Exception}");
            LogCrash(args.Exception);
            MessageBox.Show($"Ein Fehler ist aufgetreten:\n{args.Exception.Message}",
                "TypeWhisper - Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // Initialize database
        var db = _serviceProvider.GetRequiredService<ITypeWhisperDatabase>();
        db.Initialize();

        // Load settings
        var settings = _serviceProvider.GetRequiredService<ISettingsService>();
        settings.Load();

        // Setup sound service
        var soundService = _serviceProvider.GetRequiredService<SoundService>();
        soundService.IsEnabled = settings.Current.SoundFeedbackEnabled;
        settings.SettingsChanged += s => soundService.IsEnabled = s.SoundFeedbackEnabled;

        // Setup tray icon
        _trayIcon = _serviceProvider.GetRequiredService<TrayIconService>();
        _trayIcon.Initialize();
        _trayIcon.ShowSettingsRequested += (_, _) => ShowSettingsWindow();
        _trayIcon.ShowHistoryRequested += (_, _) => ShowHistoryWindow();
        _trayIcon.ShowFileTranscriptionRequested += (_, _) => ShowFileTranscriptionWindow();
        _trayIcon.ExitRequested += (_, _) => Shutdown();

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
        audio.WarmUp();

        // Start API server if enabled
        if (settings.Current.ApiServerEnabled)
        {
            var api = _serviceProvider.GetRequiredService<HttpApiService>();
            api.Start(settings.Current.ApiServerPort);
        }

        // Purge old history records on startup
        var history = _serviceProvider.GetRequiredService<IHistoryService>();
        history.PurgeOldRecords(settings.Current.HistoryRetentionDays);

        // Show onboarding if first run
        if (!settings.Current.HasCompletedOnboarding)
        {
            _welcomeWindow = _serviceProvider.GetRequiredService<WelcomeWindow>();
            _welcomeWindow.Closed += (_, _) =>
            {
                settings.Save(settings.Current with { HasCompletedOnboarding = true });
                _welcomeWindow = null;
            };
            _welcomeWindow.Show();
        }

        // Auto-load previously selected model
        if (!string.IsNullOrEmpty(settings.Current.SelectedModelId))
        {
            var modelManager = _serviceProvider.GetRequiredService<ModelManagerService>();
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
        updateService.Initialize(settings.Current.UpdateUrl);
        _ = updateService.CheckForUpdatesAsync();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _serviceProvider!.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ShowHistoryWindow()
    {
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }

        _historyWindow = _serviceProvider!.GetRequiredService<HistoryWindow>();
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
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
        services.AddSingleton<ITypeWhisperDatabase>(
            new TypeWhisperDatabase(TypeWhisperEnvironment.DatabasePath));
        services.AddSingleton<ISettingsService>(
            new SettingsService(TypeWhisperEnvironment.SettingsFilePath));

        // Engines
        services.AddSingleton<ParakeetTranscriptionEngine>();
        services.AddSingleton<ModelManagerService>();

        // Audio
        services.AddSingleton<AudioRecordingService>();
        services.AddSingleton<AudioFileService>();

        // Data services
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IDictionaryService, DictionaryService>();
        services.AddSingleton<ISnippetService, SnippetService>();
        services.AddSingleton<IProfileService, ProfileService>();

        // Translation
        services.AddSingleton<ITranslationService, TranslationService>();

        // Services
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TextInsertionService>();
        services.AddSingleton<IActiveWindowService, ActiveWindowService>();
        services.AddSingleton<SoundService>();
        services.AddSingleton<HttpApiService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<UpdateService>();

        // ViewModels
        services.AddSingleton<DictationViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ModelManagerViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<DictionaryViewModel>();
        services.AddSingleton<SnippetsViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsWindowViewModel>();
        services.AddSingleton<FileTranscriptionViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<WelcomeViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<HistoryWindow>();
        services.AddTransient<FileTranscriptionWindow>();
        services.AddTransient<WelcomeWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var logPath = System.IO.Path.Combine(TypeWhisperEnvironment.LogsPath, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n";
            System.IO.File.AppendAllText(logPath, entry);
        }
        catch { /* ignore logging failures */ }
    }
}
