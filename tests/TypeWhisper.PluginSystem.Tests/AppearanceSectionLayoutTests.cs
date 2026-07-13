namespace TypeWhisper.PluginSystem.Tests;

public sealed class AppearanceSectionLayoutTests
{
    [Fact]
    public void AppearanceSection_StyleTilesUseQuietOverlayFamilyWithoutLiveText()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

        Assert.DoesNotContain("<overlay:IndicatorStyleHost", xaml);
        Assert.True(TestFile.CountOccurrences(xaml, "Background=\"#EE11161D\"") >= 3);
        Assert.True(TestFile.CountOccurrences(xaml, "BorderBrush=\"#24FFFFFF\"") >= 3);
        Assert.Contains("CornerRadius=\"22\"", xaml);
        Assert.Contains("CornerRadius=\"0,0,12,12\"", xaml);
        Assert.Contains("CornerRadius=\"21\"", xaml);
        Assert.DoesNotContain("Appearance.PreviewLiveText", xaml);
        Assert.DoesNotContain("Appearance.PreviewTitle", xaml);
    }

    [Fact]
    public void AppearanceSection_AutoHideSliderLivesInOverlayLayoutBlock()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

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
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

        Assert.Contains("x:Name=\"LeftWidgetSettingsRow\"", xaml);
        Assert.Contains("x:Name=\"RightWidgetSettingsRow\"", xaml);
        Assert.Contains("x:Name=\"WidgetSettingsSeparatorTop\"", xaml);
        Assert.Contains("x:Name=\"WidgetSettingsSeparatorMiddle\"", xaml);

        var leftRow = TestFile.ExtractBlock(xaml, "x:Name=\"LeftWidgetSettingsRow\"");
        var rightRow = TestFile.ExtractBlock(xaml, "x:Name=\"RightWidgetSettingsRow\"");
        var topSeparator = TestFile.ExtractBlock(xaml, "x:Name=\"WidgetSettingsSeparatorTop\"");
        var middleSeparator = TestFile.ExtractBlock(xaml, "x:Name=\"WidgetSettingsSeparatorMiddle\"");

        AssertWidgetSettingCollapsesForCompactBadge(leftRow);
        AssertWidgetSettingCollapsesForCompactBadge(rightRow);
        AssertWidgetSettingCollapsesForCompactBadge(topSeparator);
        AssertWidgetSettingCollapsesForCompactBadge(middleSeparator);
    }

    [Fact]
    public void AppearanceSection_PluginControlsDoNotDependOnIndicatorStyle()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

        Assert.Contains("x:Name=\"LivePreviewToggleSettingsRow\"", xaml);
        Assert.Contains("x:Name=\"LiveTextSizeSettingsRow\"", xaml);

        var previewToggle = TestFile.ExtractBlock(xaml, "x:Name=\"LivePreviewToggleSettingsRow\"");
        var liveTextSize = TestFile.ExtractBlock(xaml, "x:Name=\"LiveTextSizeSettingsRow\"");

        Assert.Contains("Settings.LiveTranscriptionEnabled", previewToggle);
        Assert.DoesNotContain("Value=\"{x:Static models:IndicatorStyle.CompactBadge}\"", previewToggle);
        Assert.DoesNotContain("Settings.IndicatorStyle", liveTextSize);
        Assert.DoesNotContain("Visibility\" Value=\"Collapsed", liveTextSize);
    }

    [Fact]
    public void AppearanceSection_OffersOnlineAsrBatchLivePreviewOptIn()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

        Assert.Contains("x:Name=\"OnlineAsrBatchLivePreviewSettingsRow\"", xaml);
        Assert.Contains("Appearance.OnlineAsrBatchLivePreview", xaml);
        Assert.Contains("Appearance.OnlineAsrBatchLivePreviewHint", xaml);
        Assert.Contains("Settings.OnlineAsrBatchLiveTranscriptionEnabled", xaml);

        var row = TestFile.ExtractBlock(xaml, "x:Name=\"OnlineAsrBatchLivePreviewSettingsRow\"");
        Assert.Contains("IsEnabled=\"{Binding Settings.LiveTranscriptionEnabled}\"", row);
    }

    [Fact]
    public void AppearanceSection_DoesNotOfferManualOverlayLocationReset()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AppearanceSection.xaml");

        Assert.DoesNotContain("Appearance.ResetOverlayLocation", xaml);
        Assert.DoesNotContain("Appearance.ResetOverlayLocationHint", xaml);
        Assert.DoesNotContain("Settings.ResetOverlayLocationCommand", xaml);
    }

    private static void AssertWidgetSettingCollapsesForCompactBadge(string xamlBlock)
    {
        Assert.Contains("Settings.IndicatorStyle", xamlBlock);
        Assert.Contains("Value=\"{x:Static models:IndicatorStyle.CompactBadge}\"", xamlBlock);
        Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\"/>", xamlBlock);
    }
}
