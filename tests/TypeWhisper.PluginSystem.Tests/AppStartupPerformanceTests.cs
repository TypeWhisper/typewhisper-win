namespace TypeWhisper.PluginSystem.Tests;

public sealed class AppStartupPerformanceTests
{
    [Fact]
    public void OnStartup_StartsAudioWarmupInBackground()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");
        var startupWarmupBlock = TestFile.ExtractBlock(source, "// Warm up audio", 520);

        Assert.Contains("StartAudioWarmUpInBackground", startupWarmupBlock);
        Assert.DoesNotContain("audio.WarmUp()", startupWarmupBlock);
    }

    [Fact]
    public void OnStartup_DoesNotEagerLoadTheSelectedLocalModel()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");
        var startup = TestFile.ExtractBlock(source, "protected override", 16000);

        Assert.DoesNotContain("Auto-load previously selected model", startup);
        Assert.DoesNotContain("modelManager.LoadModelAsync(settings.Current.SelectedModelId)", startup);
        Assert.Contains("modelManager.MigrateSettings()", startup);
    }

    [Fact]
    public void OnStartup_RendersTheAppShellBeforePluginDiscovery()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");
        var startup = TestFile.ExtractBlock(source, "protected override", 16000);

        Assert.Contains("protected override async void OnStartup", startup);
        Assert.Contains("await Dispatcher.Yield(DispatcherPriority.ContextIdle);", startup);
        Assert.Contains("await RunTrackedStartupWorkAsync(", startup);
        Assert.Contains("token => pluginManager.InitializeAsync(token)", startup);
        Assert.DoesNotContain("InitializeAsync().GetAwaiter().GetResult()", startup);

        var trayIndex = startup.IndexOf("_trayIcon.Initialize();", StringComparison.Ordinal);
        var windowIndex = startup.IndexOf("mainWindow.Show();", StringComparison.Ordinal);
        var yieldIndex = startup.IndexOf(
            "await Dispatcher.Yield(DispatcherPriority.ContextIdle);",
            StringComparison.Ordinal);
        var pluginsIndex = startup.IndexOf(
            "token => pluginManager.InitializeAsync(token)",
            StringComparison.Ordinal);

        Assert.True(
            trayIndex >= 0
            && trayIndex < windowIndex
            && windowIndex < yieldIndex
            && yieldIndex < pluginsIndex,
            "The tray and overlay shell must render before plugin discovery starts.");
    }

    [Fact]
    public void OnExit_CancelsAndDrainsTrackedStartupWorkBeforeServiceDisposal()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");
        var onExit = TestFile.ExtractBlock(source, "protected override void OnExit", 2200);

        Assert.Contains("_startupCancellation.Cancel();", onExit);
        Assert.Contains("startupTask is { IsCompleted: false }", onExit);
        Assert.Contains("startupTask.ContinueWith(", onExit);
        Assert.Contains("((ServiceProvider)state!).Dispose()", onExit);
    }

    [Fact]
    public void SettingsWindow_DefersMicrophoneRefreshAndPreviewUntilLoaded()
    {
        var window = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SettingsWindow.xaml.cs");
        var settings = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsViewModel.cs");
        var constructor = TestFile.ExtractBlock(settings, "public SettingsViewModel(", 2600);

        Assert.Contains("Loaded += OnLoaded;", window);
        Assert.Contains("EnsureMicrophonesLoadedAsync", window);
        Assert.DoesNotContain("RefreshMicrophones();", constructor);
    }

    [Fact]
    public void NonFatalStartupAndAudioFilters_UseSharedFatalExceptionFilter()
    {
        var appSource = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");
        var audioSource = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "AudioRecordingService.cs");

        var trayFilter = TestFile.ExtractBlock(appSource, "private static bool IsNonFatalTrayActionException", 180);
        var startupFilter = TestFile.ExtractBlock(appSource, "private static bool IsNonFatalStartupException", 180);
        var audioFilter = TestFile.ExtractBlock(audioSource, "private static bool IsNonFatalAudioException", 180);

        Assert.Contains("NonFatalExceptionFilter.IsNonFatal", trayFilter);
        Assert.Contains("NonFatalExceptionFilter.IsNonFatal", startupFilter);
        Assert.Contains("NonFatalExceptionFilter.IsNonFatal", audioFilter);
        Assert.DoesNotContain("OutOfMemoryException", trayFilter);
        Assert.DoesNotContain("OutOfMemoryException", startupFilter);
        Assert.DoesNotContain("OutOfMemoryException", audioFilter);
    }
}
