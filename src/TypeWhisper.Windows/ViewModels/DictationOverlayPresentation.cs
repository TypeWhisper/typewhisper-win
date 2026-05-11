using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

internal sealed record DictationResetOutcome(
    DictationState State,
    bool IsOverlayVisible,
    bool ShowFeedback,
    bool FeedbackIsError,
    bool ForceHotkeyStop);

internal static class DictationOverlayPresentation
{
    public static bool ShowInlineFeedback(bool isOverlayVisible, bool showFeedback) =>
        isOverlayVisible && showFeedback;

    public static bool ShowDetachedFeedback(bool isOverlayVisible, bool showFeedback) =>
        !isOverlayVisible && showFeedback;

    public static bool HasVisibleContent(bool isOverlayVisible, bool showFeedback) =>
        isOverlayVisible || ShowDetachedFeedback(isOverlayVisible, showFeedback);

    public static bool ShowBuiltInPartialPreview(
        string? partialText,
        bool externalLivePreviewActive,
        bool liveTranscriptionEnabled,
        IndicatorStyle indicatorStyle) =>
        liveTranscriptionEnabled
        && indicatorStyle is IndicatorStyle.StatusIsland or IndicatorStyle.EdgeDock
        && !externalLivePreviewActive
        && !string.IsNullOrWhiteSpace(partialText);

    public static DictationResetOutcome CreateTransientIdleFeedback(bool feedbackIsError = false) =>
        new(
            DictationState.Idle,
            IsOverlayVisible: false,
            ShowFeedback: true,
            FeedbackIsError: feedbackIsError,
            ForceHotkeyStop: true);
}
