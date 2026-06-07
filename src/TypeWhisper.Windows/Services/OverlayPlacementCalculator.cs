using System.Windows;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

internal enum OverlayPlacementTarget
{
    CursorMonitor,
    PrimaryMonitor
}

internal static class OverlayPlacementCalculator
{
    public static Rect SelectWorkArea(
        OverlayPlacementTarget target,
        Rect? cursorWorkArea,
        Rect? primaryWorkArea,
        Rect fallbackWorkArea) =>
        target == OverlayPlacementTarget.PrimaryMonitor
            ? primaryWorkArea ?? fallbackWorkArea
            : cursorWorkArea ?? fallbackWorkArea;

    public static Point Calculate(
        Rect workArea,
        Size actualSize,
        OverlayPosition overlayPosition,
        Size fallbackSize)
    {
        var width = PositiveOrFallback(actualSize.Width, fallbackSize.Width);
        var height = PositiveOrFallback(actualSize.Height, fallbackSize.Height);
        var left = workArea.Left + (workArea.Width - width) / 2;
        var top = overlayPosition == OverlayPosition.Top
            ? workArea.Top
            : workArea.Bottom - height;

        return new Point(
            ClampToWorkArea(left, workArea.Left, workArea.Right - width),
            ClampToWorkArea(top, workArea.Top, workArea.Bottom - height));
    }

    private static double PositiveOrFallback(double value, double fallback) =>
        value > 0 ? value : fallback;

    private static double ClampToWorkArea(double value, double minimum, double maximum) =>
        maximum < minimum
            ? minimum
            : Math.Clamp(value, minimum, maximum);
}
