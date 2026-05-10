using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void DefaultIndicatorStyle_IsStatusIsland()
    {
        Assert.Equal(IndicatorStyle.StatusIsland, AppSettings.Default.IndicatorStyle);
    }

    [Fact]
    public void DefaultPreviewBubbleAutoHideMilliseconds_IsFifteenHundred()
    {
        Assert.Equal(1500, AppSettings.Default.PreviewBubbleAutoHideMilliseconds);
    }

    [Fact]
    public void DefaultLiveTranscriptionFontSize_IsTwelve()
    {
        Assert.Equal(12d, AppSettings.Default.LiveTranscriptionFontSize);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1500, 1500)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    public void NormalizePreviewBubbleAutoHideMilliseconds_ClampsToSupportedRange(
        int value,
        int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(value));
    }

    [Theory]
    [InlineData(7.0, 10.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(13.5, 13.5)]
    [InlineData(18.0, 18.0)]
    [InlineData(21.0, 18.0)]
    public void NormalizeLiveTranscriptionFontSize_ClampsToSupportedRange(
        double value,
        double expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLiveTranscriptionFontSize(value));
    }
}
