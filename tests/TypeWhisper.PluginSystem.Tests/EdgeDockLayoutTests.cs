namespace TypeWhisper.PluginSystem.Tests;

public sealed class EdgeDockLayoutTests
{
    [Fact]
    public void EdgeDock_UsesSettingsPreviewVisualStyle()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml.cs");

        Assert.Contains("Background=\"#E6101824\"", xaml);
        Assert.Contains("BorderBrush=\"#2B7EC8FF\"", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"0,0,9,9\"/>", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"9,9,0,0\"/>", xaml);
        Assert.Contains("Padding=\"9,6\"", xaml);
        Assert.Contains("new Thickness(0, 5, 0, 0)", codeBehind);
        Assert.Contains("new Thickness(0, 0, 0, 5)", codeBehind);
    }

    [Fact]
    public void EdgeDock_AnimatesLiveTextItselfWithoutTextBlockClipping()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml.cs");

        Assert.Contains("x:Name=\"PartialPreviewBorder\"", xaml);
        var partialTextBlock = TestFile.ExtractBlock(xaml, "x:Name=\"PartialTextBlock\"", 700);
        Assert.Contains("x:Name=\"PartialTextTransform\"", partialTextBlock);
        Assert.Contains("FontSize=\"{Binding LiveTranscriptionFontSize}\"", partialTextBlock);
        Assert.DoesNotContain("Visibility=\"{Binding ShowBuiltInPartialPreview", xaml);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", partialTextBlock);
        Assert.DoesNotContain("MaxHeight=\"56\"", xaml);
        Assert.Contains("PropertyChangedEventManager.AddHandler", codeBehind);
        Assert.Contains("BeginAnimation(FrameworkElement.HeightProperty", codeBehind);
        Assert.Contains("AnimatePartialText", codeBehind);
        Assert.Contains("PartialPreviewScrollViewer.ScrollToEnd", codeBehind);
    }

    [Fact]
    public void EdgeDock_DoesNotScaleTextContainerWithAudioLevel()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml");

        var dockBlock = TestFile.ExtractBlock(xaml, "x:Name=\"DockBorder\"", 700);

        Assert.DoesNotContain("AudioLevelScale", xaml);
        Assert.DoesNotContain("<Border.RenderTransform>", dockBlock);
        Assert.DoesNotContain("ScaleTransform", dockBlock);
    }
}
