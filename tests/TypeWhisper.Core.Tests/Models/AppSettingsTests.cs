using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void DefaultPreviewBubbleAutoHideMilliseconds_IsFifteenHundred()
    {
        Assert.Equal(1500, AppSettings.Default.PreviewBubbleAutoHideMilliseconds);
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
}
