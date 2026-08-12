using System.Windows;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OverlayPlacementCalculatorTests
{
    private static readonly Size FallbackSize = new(300, 50);

    [Fact]
    public void Calculate_CentersOverlayAtTopOfWorkArea()
    {
        var point = OverlayPlacementCalculator.Calculate(
            new Rect(100, 50, 1000, 700),
            new Size(200, 80),
            OverlayPosition.Top,
            FallbackSize);

        Assert.Equal(500, point.X);
        Assert.Equal(50, point.Y);
    }

    [Fact]
    public void Calculate_CentersOverlayAtBottomOfWorkArea()
    {
        var point = OverlayPlacementCalculator.Calculate(
            new Rect(100, 50, 1000, 700),
            new Size(200, 80),
            OverlayPosition.Bottom,
            FallbackSize);

        Assert.Equal(500, point.X);
        Assert.Equal(670, point.Y);
    }

    [Fact]
    public void Calculate_ClampsOversizedOverlayInsideWorkArea()
    {
        var point = OverlayPlacementCalculator.Calculate(
            new Rect(10, 20, 100, 80),
            new Size(300, 120),
            OverlayPosition.Bottom,
            FallbackSize);

        Assert.Equal(10, point.X);
        Assert.Equal(20, point.Y);
    }

    [Fact]
    public void Calculate_UsesFallbackSizeWhenActualSizeIsUnknown()
    {
        var point = OverlayPlacementCalculator.Calculate(
            new Rect(100, 50, 1000, 700),
            new Size(0, 0),
            OverlayPosition.Bottom,
            FallbackSize);

        Assert.Equal(450, point.X);
        Assert.Equal(700, point.Y);
    }

    [Fact]
    public void SelectWorkArea_UsesPrimaryWorkAreaForPrimaryResetTarget()
    {
        var cursorWorkArea = new Rect(-1920, 0, 1920, 1040);
        var primaryWorkArea = new Rect(0, 0, 2560, 1400);
        var fallbackWorkArea = new Rect(0, 0, 1280, 720);

        var selected = OverlayPlacementCalculator.SelectWorkArea(
            OverlayPlacementTarget.PrimaryMonitor,
            cursorWorkArea,
            primaryWorkArea,
            fallbackWorkArea);

        Assert.Equal(primaryWorkArea, selected);
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1040, -1110, 990)]
    [InlineData(0, -1200, 1920, 1200, 810, -50)]
    public void Calculate_UsesNegativeMonitorCoordinates(
        double left,
        double top,
        double width,
        double height,
        double expectedX,
        double expectedY)
    {
        var point = OverlayPlacementCalculator.Calculate(
            new Rect(left, top, width, height),
            new Size(300, 50),
            OverlayPosition.Bottom,
            FallbackSize);

        Assert.Equal(expectedX, point.X);
        Assert.Equal(expectedY, point.Y);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Calculate_CentersEquivalentDipGeometryAtCommonDisplayScales(double scale)
    {
        var workArea = new Rect(-1600 / scale, 80 / scale, 1600 / scale, 900 / scale);
        var overlaySize = new Size(240 / scale, 60 / scale);

        var point = OverlayPlacementCalculator.Calculate(
            workArea,
            overlaySize,
            OverlayPosition.Top,
            FallbackSize);

        Assert.Equal(-920 / scale, point.X, 6);
        Assert.Equal(80 / scale, point.Y, 6);
    }

    [Fact]
    public void SelectWorkArea_UsesCursorMonitorForNewVisibleRecording()
    {
        var cursorWorkArea = new Rect(-1280, -720, 1280, 680);
        var primaryWorkArea = new Rect(0, 0, 1920, 1040);
        var fallbackWorkArea = new Rect(0, 0, 1280, 720);

        var selected = OverlayPlacementCalculator.SelectWorkArea(
            OverlayPlacementTarget.CursorMonitor,
            cursorWorkArea,
            primaryWorkArea,
            fallbackWorkArea);

        Assert.Equal(cursorWorkArea, selected);
    }
}
