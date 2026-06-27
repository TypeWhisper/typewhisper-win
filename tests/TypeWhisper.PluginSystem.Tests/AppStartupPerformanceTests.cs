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
}
