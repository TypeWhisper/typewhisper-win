namespace TypeWhisper.PluginSystem.Tests;

public sealed class CompactBadgeLayoutTests
{
    [Fact]
    public void CompactBadge_ShowsOnlyTheRecordIndicator()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "Overlay",
            "CompactBadgeIndicatorView.xaml");

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
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

        var tileBlock = TestFile.ExtractBlock(xaml, "ConverterParameter={x:Static models:IndicatorStyle.CompactBadge}", 1400);
        var previewBlock = TestFile.ExtractBlock(xaml, "Value=\"{x:Static models:IndicatorStyle.CompactBadge}\"", 1400);

        Assert.DoesNotContain("Appearance.PreviewStatus", tileBlock);
        Assert.DoesNotContain("Settings.OverlayRightWidget", tileBlock);
        Assert.DoesNotContain("Appearance.PreviewStatus", previewBlock);
        Assert.DoesNotContain("Settings.OverlayRightWidget", previewBlock);
    }
}
