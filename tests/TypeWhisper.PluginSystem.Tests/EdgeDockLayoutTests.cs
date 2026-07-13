namespace TypeWhisper.PluginSystem.Tests;

public sealed class EdgeDockLayoutTests
{
    [Fact]
    public void EdgeDock_UsesQuietOverlayFamilyStyleAtEitherEdge()
    {
        var xaml = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "EdgeDockIndicatorView.xaml");

        Assert.Contains("Background=\"#EE11161D\"", xaml);
        Assert.Contains("BorderBrush=\"#24FFFFFF\"", xaml);
        Assert.Contains("MinWidth=\"360\"", xaml);
        Assert.Contains("MinHeight=\"40\"", xaml);
        Assert.Contains("Padding=\"14,8\"", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"0,0,12,12\"/>", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"12,12,0,0\"/>", xaml);
        Assert.Contains("Foreground=\"#E8FFFFFF\"", xaml);
    }

    [Fact]
    public void EdgeDock_HasNoBuiltInTranscriptOrShellScaling()
    {
        var xaml = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "EdgeDockIndicatorView.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "EdgeDockIndicatorView.xaml.cs");

        var dock = TestFile.ExtractBlock(xaml, "x:Name=\"DockBorder\"", 700);
        Assert.DoesNotContain("PartialText", xaml);
        Assert.DoesNotContain("LiveTranscriptionFontSize", xaml);
        Assert.DoesNotContain("<Border.RenderTransform>", dock);
        Assert.DoesNotContain("PropertyChangedEventManager", codeBehind);
        Assert.DoesNotContain("DoubleAnimation", codeBehind);
    }
}
