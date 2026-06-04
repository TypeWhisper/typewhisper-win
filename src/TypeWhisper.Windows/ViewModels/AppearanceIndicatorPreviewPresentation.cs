using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

internal static class AppearanceIndicatorPreviewPresentation
{
    /// <summary>
    /// Returns whether show partial text.
    /// </summary>
    public static bool ShouldShowPartialText(bool liveTranscriptionEnabled, IndicatorStyle indicatorStyle) =>
        liveTranscriptionEnabled
        && indicatorStyle is IndicatorStyle.StatusIsland or IndicatorStyle.EdgeDock;
}
