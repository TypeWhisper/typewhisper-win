using System.Globalization;
using System.Windows.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Converters;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class EdgeDockStatusDockConverterTests
{
    [Fact]
    public void Convert_TopPosition_DocksStatusAtTopEdge()
    {
        var converter = new EdgeDockStatusDockConverter();

        var dock = converter.Convert(OverlayPosition.Top, typeof(Dock), "", CultureInfo.InvariantCulture);

        Assert.Equal(Dock.Top, dock);
    }

    [Fact]
    public void Convert_BottomPosition_DocksStatusAtBottomEdge()
    {
        var converter = new EdgeDockStatusDockConverter();

        var dock = converter.Convert(OverlayPosition.Bottom, typeof(Dock), "", CultureInfo.InvariantCulture);

        Assert.Equal(Dock.Bottom, dock);
    }
}
