namespace TypeWhisper.PluginSystem.Tests;

public sealed class StatusIslandLayoutTests
{
    [Fact]
    public void StatusIsland_UsesQuietOverlayFamilyStyle()
    {
        var xaml = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "DictationOverlayView.xaml");

        Assert.Contains("CornerRadius=\"22\"", xaml);
        Assert.Contains("Background=\"#EE11161D\"", xaml);
        Assert.Contains("BorderBrush=\"#24FFFFFF\"", xaml);
        Assert.Contains("MinWidth=\"220\"", xaml);
        Assert.Contains("MinHeight=\"44\"", xaml);
        Assert.Contains("Padding=\"14,9\"", xaml);
        Assert.Contains("Foreground=\"#E8FFFFFF\"", xaml);
    }

    [Fact]
    public void StatusIsland_HasNoBuiltInTranscriptOrShellScaling()
    {
        var xaml = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "DictationOverlayView.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "DictationOverlayView.xaml.cs");

        var island = TestFile.ExtractBlock(xaml, "x:Name=\"IslandBorder\"", 700);
        Assert.DoesNotContain("PartialText", xaml);
        Assert.DoesNotContain("LiveTranscriptionFontSize", xaml);
        Assert.DoesNotContain("<Border.RenderTransform>", island);
        Assert.DoesNotContain("PropertyChangedEventManager", codeBehind);
        Assert.DoesNotContain("DoubleAnimation", codeBehind);
    }
}
