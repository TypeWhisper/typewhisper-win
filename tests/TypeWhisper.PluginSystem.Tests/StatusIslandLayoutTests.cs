using System.IO;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class StatusIslandLayoutTests
{
    [Fact]
    public void StatusIsland_AnchorsPartialTextByOverlayPositionWithoutWidthAnimation()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml"));
        var codeBehind = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml.cs"));

        Assert.DoesNotContain("PartialTextHalf", xaml);
        Assert.DoesNotContain("ConverterParameter=top", xaml);
        Assert.DoesNotContain("ConverterParameter=bottom", xaml);
        Assert.Contains("DownwardPartialPreview", xaml);
        Assert.Contains("<DockPanel", xaml);
        Assert.Contains("DockPanel.Dock=\"{Binding OverlayPosition, Converter={StaticResource EdgeDockStatusDock}}\"", xaml);
        Assert.Contains("TranslateTransform", xaml);
        Assert.Contains("DoubleAnimation", codeBehind);
        Assert.Contains("FrameworkElement.HeightProperty", codeBehind);
        Assert.Contains("QuinticEase", codeBehind);
        Assert.Contains("OverlayPosition.Bottom", codeBehind);
        Assert.Contains("new Thickness(0, 5, 0, 0)", codeBehind);
        Assert.Contains("new Thickness(0, 0, 0, 5)", codeBehind);
        Assert.DoesNotContain("Storyboard.TargetProperty=\"MaxWidth\"", xaml);
        Assert.DoesNotContain("InlinePartialPreview", xaml);
    }

    [Fact]
    public void StatusIsland_UsesSettingsPreviewVisualStyle()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml"));

        Assert.Contains("CornerRadius=\"17\"", xaml);
        Assert.Contains("Background=\"#E6121822\"", xaml);
        Assert.Contains("BorderBrush=\"#22507BAA\"", xaml);
        Assert.Contains("Padding=\"10,7\"", xaml);
        Assert.Contains("MinWidth=\"172\"", xaml);
        Assert.DoesNotContain("Width=\"420\"", xaml);
        Assert.DoesNotContain("Background=\"#10FFFFFF\"", xaml);
    }

    [Fact]
    public void StatusIsland_DoesNotScaleTextContainerWithAudioLevel()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml"));

        var islandBlock = ExtractBlock(xaml, "x:Name=\"IslandBorder\"");

        Assert.DoesNotContain("AudioLevelScale", xaml);
        Assert.DoesNotContain("<Border.RenderTransform>", islandBlock);
        Assert.DoesNotContain("ScaleTransform", islandBlock);
    }

    [Fact]
    public void StatusIsland_UsesRuntimeHeightAnimationForLivePartialPreview()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml"));
        var codeBehind = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml.cs"));

        Assert.Contains("x:Name=\"PartialTextBlock\"", xaml);
        Assert.Contains("Height=\"0\"", xaml);
        Assert.DoesNotContain("<DataTrigger.EnterActions>", xaml);
        Assert.Contains("PropertyChangedEventManager.AddHandler", codeBehind);
        Assert.Contains("BeginAnimation(FrameworkElement.HeightProperty", codeBehind);
        Assert.Contains("\"ShowBuiltInPartialPreview\"", codeBehind);
        Assert.Contains("\"PartialText\"", codeBehind);
        Assert.Contains("\"OverlayPosition\"", codeBehind);
    }

    [Fact]
    public void StatusIsland_AnimatesLiveTextItselfWithoutTextBlockClipping()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml"));
        var codeBehind = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "DictationOverlayView.xaml.cs"));

        var partialTextBlock = ExtractBlock(xaml, "x:Name=\"PartialTextBlock\"");
        Assert.Contains("x:Name=\"PartialTextTransform\"", partialTextBlock);
        Assert.Contains("FontSize=\"{Binding LiveTranscriptionFontSize}\"", partialTextBlock);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", partialTextBlock);
        Assert.DoesNotContain("MaxHeight=\"72\"", partialTextBlock);
        Assert.Contains("MaxPartialPreviewHeight = 124", codeBehind);
        Assert.Contains("AnimatePartialText", codeBehind);
        Assert.Contains("PartialTextTransform", codeBehind);
        Assert.Contains("UIElement.OpacityProperty", codeBehind);
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
        var length = Math.Min(600, text.Length - start);
        return text.Substring(start, length);
    }
}
