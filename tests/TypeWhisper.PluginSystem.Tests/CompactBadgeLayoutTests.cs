using System.IO;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CompactBadgeLayoutTests
{
    [Fact]
    public void CompactBadge_ShowsOnlyTheRecordIndicator()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "CompactBadgeIndicatorView.xaml"));

        Assert.Contains("<overlay:IndicatorWidget", xaml);
        Assert.Contains("MinWidth=\"34\"", xaml);
        Assert.Contains("MinHeight=\"34\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding StatusText}\"", xaml);
        Assert.DoesNotContain("WidgetType=\"{Binding RightWidget}\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding FeedbackText}\"", xaml);
    }

    [Fact]
    public void AppearanceSection_CompactBadgePreviewShowsOnlyTheRecordIndicator()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml"));

        var tileBlock = ExtractBlock(xaml, "ConverterParameter={x:Static models:IndicatorStyle.CompactBadge}");
        var previewBlock = ExtractBlock(xaml, "Value=\"{x:Static models:IndicatorStyle.CompactBadge}\"");

        Assert.DoesNotContain("Appearance.PreviewStatus", tileBlock);
        Assert.DoesNotContain("Settings.OverlayRightWidget", tileBlock);
        Assert.DoesNotContain("Appearance.PreviewStatus", previewBlock);
        Assert.DoesNotContain("Settings.OverlayRightWidget", previewBlock);
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
        var length = Math.Min(1400, text.Length - start);
        return text.Substring(start, length);
    }
}
