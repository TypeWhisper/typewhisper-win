namespace TypeWhisper.PluginSystem.Tests;

public sealed class CompactBadgeLayoutTests
{
    [Fact]
    public void CompactBadge_UsesWaveformForRecordingAndStatusForOtherStates()
    {
        var xaml = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Controls", "Overlay", "CompactBadgeIndicatorView.xaml");

        Assert.Contains("x:Name=\"BadgeBorder\"", xaml);
        Assert.Contains("Background=\"#EE11161D\"", xaml);
        Assert.Contains("BorderBrush=\"#24FFFFFF\"", xaml);
        Assert.Contains("CornerRadius=\"21\"", xaml);
        Assert.Contains("Width=\"42\"", xaml);
        Assert.Contains("Height=\"42\"", xaml);
        Assert.Contains("<overlay:WaveformWidget", xaml);
        Assert.Contains("<overlay:IndicatorWidget", xaml);
        Assert.Contains("DictationState.Recording", xaml);
        Assert.DoesNotContain("<Border.RenderTransform>", xaml);
        Assert.DoesNotContain("Text=\"{Binding StatusText}\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding FeedbackText}\"", xaml);
    }

    [Fact]
    public void AppearanceSection_CompactBadgePreviewUsesWaveform()
    {
        var xaml = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Views", "Sections", "AppearanceSection.xaml");
        var tile = TestFile.ExtractBlock(
            xaml, "ConverterParameter={x:Static models:IndicatorStyle.CompactBadge}", 1600);

        Assert.Contains("<overlay:WaveformWidget", tile);
        Assert.DoesNotContain("Appearance.PreviewStatus", tile);
        Assert.DoesNotContain("Settings.OverlayRightWidget", tile);
    }
}
