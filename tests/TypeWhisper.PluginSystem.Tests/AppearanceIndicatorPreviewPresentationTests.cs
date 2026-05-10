using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AppearanceIndicatorPreviewPresentationTests
{
    [Theory]
    [InlineData(true, IndicatorStyle.StatusIsland, true)]
    [InlineData(true, IndicatorStyle.EdgeDock, true)]
    [InlineData(true, IndicatorStyle.CompactBadge, false)]
    [InlineData(false, IndicatorStyle.StatusIsland, false)]
    [InlineData(false, IndicatorStyle.EdgeDock, false)]
    public void ShouldShowPartialText_OnlyForLiveIslandAndEdge(
        bool liveTranscriptionEnabled,
        IndicatorStyle indicatorStyle,
        bool expected)
    {
        var actual = AppearanceIndicatorPreviewPresentation.ShouldShowPartialText(
            liveTranscriptionEnabled,
            indicatorStyle);

        Assert.Equal(expected, actual);
    }
}
