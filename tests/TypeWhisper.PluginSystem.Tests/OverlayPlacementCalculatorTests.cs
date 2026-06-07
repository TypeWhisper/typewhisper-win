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
}
