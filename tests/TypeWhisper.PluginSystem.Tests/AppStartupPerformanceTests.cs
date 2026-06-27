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
