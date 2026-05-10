using System.IO;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class EdgeDockLayoutTests
{
    [Fact]
    public void EdgeDock_UsesSettingsPreviewVisualStyle()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml"));
        var codeBehind = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml.cs"));

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
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml"));
        var codeBehind = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml.cs"));

        Assert.Contains("x:Name=\"PartialPreviewBorder\"", xaml);
        var partialTextBlock = ExtractBlock(xaml, "x:Name=\"PartialTextBlock\"");
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
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "EdgeDockIndicatorView.xaml"));

        var dockBlock = ExtractBlock(xaml, "x:Name=\"DockBorder\"");

        Assert.DoesNotContain("AudioLevelScale", xaml);
        Assert.DoesNotContain("<Border.RenderTransform>", dockBlock);
        Assert.DoesNotContain("ScaleTransform", dockBlock);
    }

    private static string ProjectFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Join([directory.FullName, .. parts]);
    }

    private static string ExtractBlock(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find {marker}.");
        var length = Math.Min(700, text.Length - start);
        return text.Substring(start, length);
    }
}
