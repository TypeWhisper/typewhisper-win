using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LiveTextPlacementTests
{
    [Theory]
    [InlineData((int)LiveTextResizeEdge.Right)]
    [InlineData((int)LiveTextResizeEdge.Left)]
    [InlineData((int)LiveTextResizeEdge.Top)]
    [InlineData((int)LiveTextResizeEdge.Bottom)]
    [InlineData((int)(LiveTextResizeEdge.Right | LiveTextResizeEdge.Bottom))]
    public void Resize_AfterWorkAreaShrinksNormalizesWindowAndMinimums(int edge)
    {
        var work = new LiveTextBounds(-200, -100, 200, 100);
        var result = LiveTextPlacement.Resize(new(-200, -100, 400, 300), (LiveTextResizeEdge)edge,
            80, 60, 280, 140, work);
        Assert.Equal(work, result);
    }

    [Fact]
    public void ScreenMap_UsesTargetMonitorOriginAndUniformScale()
    {
        var frame = new LiveTextPreviewFrame(new(-1440, 270, 480, 270), new(-1920, 0, 1920, 1080));
        var map = LiveTextPlacement.Project(frame, 640, 180);
        Assert.Equal(new(160, 0, 320, 180), map.Screen);
        Assert.Equal(new(240, 45, 80, 45), map.Window);
    }

    [Fact]
    public void ScreenMap_PortraitMonitorAndWindowFillingWorkAreaStayAligned()
    {
        var work = new LiveTextBounds(2560, -1920, 1080, 1920);
        var map = LiveTextPlacement.Project(new(work, work), 200, 400);
        Assert.Equal(map.Screen, map.Window);
        Assert.Equal(200, map.Screen.Width);
        Assert.InRange(map.Screen.Y, 22, 23);
        Assert.InRange(map.Screen.Height, 355, 356);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 100)]
    public void ScreenMap_BeforeLayoutHasNoInvalidDimensions(double width, double height)
    {
        Assert.Equal(default, LiveTextPlacement.Project(new(new(0, 0, 420, 220), new(0, 0, 1920, 1080)), width, height));
    }

    [Theory]
    [InlineData(-2500, -900, -1920, 0)]
    [InlineData(-10, 1000, -420, 820)]
    [InlineData(-900, 300, -900, 300)]
    public void FloatingWindow_StaysInsideMonitorWorkArea(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new(expectedX, expectedY), LiveTextPlacement.Clamp(new(x, y), 420, 220, -1920, 0, 1920, 1040));
    }

    [Fact]
    public void RemovedMonitorPosition_IsRecoveredToNearestWorkArea()
    {
        Assert.Equal(new(0, 0), LiveTextPlacement.Clamp(new(-2000, -500), 420, 220, 0, 0, 1920, 1040));
        Assert.Equal(new(0, 0), LiveTextPlacement.Clamp(new(100, 100), 420, 220, 0, 0, 200, 100));
    }

    [Fact]
    public void FloatingPreferenceAndDraggedPosition_PersistSeparately()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-text-test-" + Guid.NewGuid());
        try
        {
            var preferences = new OverlayPreferences(OverlayMode.Compact, true, false, FloatingLiveText: true, LiveTranscriptionFontSize: 18);
            OverlayPreferencesStore.Save(Path.Combine(directory, "overlay.json"), preferences);
            Assert.Equal(preferences, OverlayPreferencesStore.Read(Path.Combine(directory, "overlay.json")));
            var path = Path.Combine(directory, "position.json");
            Assert.Null(LiveTextPlacement.Read(path));
            LiveTextPlacement.Save(path, new(-1234, 567));
            Assert.Equal(new(-1234, 567), LiveTextPlacement.Read(path));
            File.WriteAllText(path, "broken json");
            Assert.Null(LiveTextPlacement.Read(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData((int)(LiveTextResizeEdge.Left), 150, 100, 350, 220)]
    [InlineData((int)(LiveTextResizeEdge.Right), 100, 100, 450, 220)]
    [InlineData((int)(LiveTextResizeEdge.Top), 100, 130, 400, 190)]
    [InlineData((int)(LiveTextResizeEdge.Bottom), 100, 100, 400, 250)]
    [InlineData((int)(LiveTextResizeEdge.Top | LiveTextResizeEdge.Left), 150, 130, 350, 190)]
    [InlineData((int)(LiveTextResizeEdge.Top | LiveTextResizeEdge.Right), 100, 130, 450, 190)]
    [InlineData((int)(LiveTextResizeEdge.Bottom | LiveTextResizeEdge.Left), 150, 100, 350, 250)]
    [InlineData((int)(LiveTextResizeEdge.Bottom | LiveTextResizeEdge.Right), 100, 100, 450, 250)]
    public void Resize_MovesOnlySelectedEdges(int edge, int x, int y, int width, int height)
    {
        var result = LiveTextPlacement.Resize(new(100, 100, 400, 220), (LiveTextResizeEdge)edge, 50, 30, 280, 140, new(0, 0, 1920, 1040));
        Assert.Equal(new(x, y, width, height), result);
    }

    [Fact]
    public void Resize_ClampsToMinimumWithoutMovingOppositeCorner()
    {
        var result = LiveTextPlacement.Resize(new(100, 100, 400, 220),
            LiveTextResizeEdge.Left | LiveTextResizeEdge.Top, 10000, 10000, 280, 140, new(0, 0, 1920, 1040));
        Assert.Equal(new(220, 180, 280, 140), result);
        result = LiveTextPlacement.Resize(new(100, 100, 400, 220),
            LiveTextResizeEdge.Right | LiveTextResizeEdge.Bottom, -10000, -10000, 280, 140, new(0, 0, 1920, 1040));
        Assert.Equal(new(100, 100, 280, 140), result);
    }

    [Fact]
    public void Resize_StaysOnNegativeCoordinateDisplayAndFitsSmallWorkArea()
    {
        var result = LiveTextPlacement.Resize(new(-1600, -900, 600, 400),
            LiveTextResizeEdge.Left | LiveTextResizeEdge.Top, -10000, -10000, 420, 210, new(-1920, -1080, 1920, 1040));
        Assert.Equal(new(-1920, -1080, 920, 580), result);
        result = LiveTextPlacement.Resize(new(0, 0, 200, 100),
            LiveTextResizeEdge.Right | LiveTextResizeEdge.Bottom, 1000, 1000, 280, 140, new(0, 0, 200, 100));
        Assert.Equal(new(0, 0, 200, 100), result);
    }

    [Fact]
    public void SavedSize_PreservesLogicalDimensionsAndAcceptsLegacyPosition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-text-size-test-" + Guid.NewGuid());
        var path = Path.Combine(directory, "position.json");
        try
        {
            LiveTextPlacement.Save(path, new(-1200, 200, 640, 360));
            Assert.Equal(new(-1200, 200, 640, 360), LiveTextPlacement.Read(path));
            Assert.Equal(new(0, 0, 640, 360), LiveTextPlacement.Clamp(new(-1200, -100, 640, 360), 960, 540, 0, 0, 1920, 1040));
            File.WriteAllText(path, """{"X":20,"Y":30}""");
            Assert.Equal(new(20, 30, 420, 220), LiveTextPlacement.Read(path));
            File.WriteAllText(path, """{"X":20,"Y":30,"Width":-1,"Height":0}""");
            Assert.Equal(new(20, 30, 420, 220), LiveTextPlacement.Read(path));
            File.WriteAllText(path, """{"X":20,"Y":30,"Width":10,"Height":10000000}""");
            Assert.Equal(new(20, 30, 280, 8192), LiveTextPlacement.Read(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
