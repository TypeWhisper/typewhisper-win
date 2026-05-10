using System.IO;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AppearanceSectionLayoutTests
{
    [Fact]
    public void AppearanceSection_StyleTilesRemainTheOverlayDesignSource()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml"));

        Assert.DoesNotContain("<overlay:DictationOverlayView", xaml);
        Assert.DoesNotContain("<overlay:EdgeDockIndicatorView", xaml);
        Assert.DoesNotContain("<overlay:CompactBadgeIndicatorView", xaml);
        Assert.Contains("Background=\"#E6121822\"", xaml);
        Assert.Contains("BorderBrush=\"#22507BAA\"", xaml);
        Assert.Contains("CornerRadius=\"17\"", xaml);
        Assert.Contains("Background=\"#E6101824\"", xaml);
        Assert.Contains("CornerRadius=\"0,0,9,9\"", xaml);
        Assert.True(
            CountOccurrences(xaml, "FontSize=\"{Binding Settings.LiveTranscriptionFontSize}\"") >= 2,
            "Status Island and Edge Dock preview live text should both bind to the live text size setting.");
    }

    [Fact]
    public void AppearanceSection_AutoHideSliderLivesInOverlayLayoutBlock()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml"));

        Assert.DoesNotContain("Text=\"{loc:Str General.Preview}\"", xaml);

        var autoHideIndex = xaml.IndexOf("Appearance.AutoHideDelay", StringComparison.Ordinal);
        var positionIndex = xaml.IndexOf("General.Position", StringComparison.Ordinal);

        Assert.True(autoHideIndex >= 0, "Auto-hide delay control should be present.");
        Assert.True(positionIndex > autoHideIndex, "Auto-hide delay should appear above position controls.");
        Assert.Contains("Value=\"{Binding Settings.PreviewBubbleAutoHideSeconds", xaml);
    }

    [Fact]
    public void AppearanceSection_HidesWidgetRowsForCompactBadge()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml"));

        Assert.Contains("x:Name=\"LeftWidgetSettingsRow\"", xaml);
        Assert.Contains("x:Name=\"RightWidgetSettingsRow\"", xaml);
        Assert.Contains("x:Name=\"WidgetSettingsSeparatorTop\"", xaml);
        Assert.Contains("x:Name=\"WidgetSettingsSeparatorMiddle\"", xaml);

        var leftRow = ExtractBlock(xaml, "x:Name=\"LeftWidgetSettingsRow\"");
        var rightRow = ExtractBlock(xaml, "x:Name=\"RightWidgetSettingsRow\"");
        var topSeparator = ExtractBlock(xaml, "x:Name=\"WidgetSettingsSeparatorTop\"");
        var middleSeparator = ExtractBlock(xaml, "x:Name=\"WidgetSettingsSeparatorMiddle\"");

        AssertWidgetSettingCollapsesForCompactBadge(leftRow);
        AssertWidgetSettingCollapsesForCompactBadge(rightRow);
        AssertWidgetSettingCollapsesForCompactBadge(topSeparator);
        AssertWidgetSettingCollapsesForCompactBadge(middleSeparator);
    }

    [Fact]
    public void AppearanceSection_HidesLiveTextSizeForCompactBadgeButKeepsPreviewToggle()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml"));

        Assert.Contains("x:Name=\"LivePreviewToggleSettingsRow\"", xaml);
        Assert.Contains("x:Name=\"LiveTextSizeSettingsRow\"", xaml);

        var previewToggle = ExtractBlock(xaml, "x:Name=\"LivePreviewToggleSettingsRow\"");
        var liveTextSize = ExtractBlock(xaml, "x:Name=\"LiveTextSizeSettingsRow\"");

        Assert.Contains("Settings.LiveTranscriptionEnabled", previewToggle);
        Assert.DoesNotContain("Value=\"{x:Static models:IndicatorStyle.CompactBadge}\"", previewToggle);
        AssertWidgetSettingCollapsesForCompactBadge(liveTextSize);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertWidgetSettingCollapsesForCompactBadge(string xamlBlock)
    {
        Assert.Contains("Settings.IndicatorStyle", xamlBlock);
        Assert.Contains("Value=\"{x:Static models:IndicatorStyle.CompactBadge}\"", xamlBlock);
        Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\"/>", xamlBlock);
    }

    private static string ExtractBlock(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find {marker}.");
        var length = Math.Min(1800, text.Length - start);
        return text.Substring(start, length);
    }

    private static string ProjectFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Join([directory.FullName, .. parts]);
    }
}
