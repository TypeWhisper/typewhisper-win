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
        Assert.Contains("LiveTranscriptionFontSizeProperty = \"LiveTranscriptionFontSize\"", codeBehind);
        Assert.Contains("or LiveTranscriptionFontSizeProperty", codeBehind);
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

    [Fact]
    public void EdgeDock_DoesNotRestartTextAnimationForEveryPartialTextUpdate()
    {
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml.cs");

        var handler = TestFile.ExtractBlock(codeBehind, "private void OnDataContextPropertyChanged", 1000);

        Assert.Contains("or PartialTextProperty", handler);
        Assert.Contains("previewVisibilityChanged", handler);
        Assert.Contains("e.PropertyName == ShowBuiltInPartialPreviewProperty", handler);
        Assert.DoesNotContain("e.PropertyName == PartialTextProperty));", handler);
    }

    [Fact]
    public void EdgeDock_DoesNotRestartPreviewAnimationForEveryPartialTextUpdate()
    {
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml.cs");

        var handler = TestFile.ExtractBlock(codeBehind, "private void OnDataContextPropertyChanged", 1000);

        Assert.Contains("animatePreview", handler);
        Assert.Contains("animated: IsVisible && animatePreview", handler);
        Assert.DoesNotContain("animated: IsVisible,", handler);
    }
}
