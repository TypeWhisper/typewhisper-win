using TypeWhisper.Windows.Controls.Overlay;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WaveformWidgetTests
{
    [Fact]
    public void TargetHeights_AreClampedSymmetricAndLevelResponsive()
    {
        var silent = Enumerable.Range(0, 5)
            .Select(index => WaveformWidget.CalculateTargetHeight(-1, index))
            .ToArray();
        var speaking = Enumerable.Range(0, 5)
            .Select(index => WaveformWidget.CalculateTargetHeight(0.2f, index))
            .ToArray();
        var loud = Enumerable.Range(0, 5)
            .Select(index => WaveformWidget.CalculateTargetHeight(2, index))
            .ToArray();

        Assert.All(silent, height => Assert.Equal(3, height));
        Assert.Equal(speaking[0], speaking[4]);
        Assert.Equal(speaking[1], speaking[3]);
        Assert.True(speaking[2] > speaking[1]);
        Assert.True(loud[2] > speaking[2]);
        Assert.Equal(17, loud[2]);
    }
}
