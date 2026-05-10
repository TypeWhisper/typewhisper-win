using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class DictationOverlayPresentationTests
{
    [Fact]
    public void CreateTransientIdleFeedback_HidesOverlayAndForcesHotkeyReset()
    {
        var outcome = DictationOverlayPresentation.CreateTransientIdleFeedback();

        Assert.Equal(DictationState.Idle, outcome.State);
        Assert.False(outcome.IsOverlayVisible);
        Assert.True(outcome.ShowFeedback);
        Assert.False(outcome.FeedbackIsError);
        Assert.True(outcome.ForceHotkeyStop);
    }

    [Fact]
    public void DetachedFeedback_IsShownOnlyWhenOverlayIsHidden()
    {
        Assert.True(DictationOverlayPresentation.ShowDetachedFeedback(
            isOverlayVisible: false,
            showFeedback: true));
        Assert.False(DictationOverlayPresentation.ShowDetachedFeedback(
            isOverlayVisible: true,
            showFeedback: true));
        Assert.False(DictationOverlayPresentation.ShowDetachedFeedback(
            isOverlayVisible: false,
            showFeedback: false));
    }

    [Fact]
    public void VisibleContent_RemainsVisibleForDetachedFeedback()
    {
        Assert.True(DictationOverlayPresentation.HasVisibleContent(
            isOverlayVisible: false,
            showFeedback: true));
        Assert.True(DictationOverlayPresentation.HasVisibleContent(
            isOverlayVisible: true,
            showFeedback: false));
        Assert.False(DictationOverlayPresentation.HasVisibleContent(
            isOverlayVisible: false,
            showFeedback: false));
    }

    [Fact]
    public void BuiltInPartialPreview_ShowsWhenExternalPreviewIsInactive()
    {
        Assert.True(DictationOverlayPresentation.ShowBuiltInPartialPreview(
            "confirmed live text",
            externalLivePreviewActive: false,
            liveTranscriptionEnabled: true,
            indicatorStyle: IndicatorStyle.StatusIsland));
    }

    [Fact]
    public void BuiltInPartialPreview_HidesWhenExternalPreviewIsActive()
    {
        Assert.False(DictationOverlayPresentation.ShowBuiltInPartialPreview(
            "confirmed live text",
            externalLivePreviewActive: true,
            liveTranscriptionEnabled: true,
            indicatorStyle: IndicatorStyle.StatusIsland));
    }

    [Fact]
    public void BuiltInPartialPreview_HidesBlankText()
    {
        Assert.False(DictationOverlayPresentation.ShowBuiltInPartialPreview(
            "   ",
            externalLivePreviewActive: false,
            liveTranscriptionEnabled: true,
            indicatorStyle: IndicatorStyle.StatusIsland));
    }

    [Fact]
    public void BuiltInPartialPreview_HidesWhenLiveTranscriptionIsDisabled()
    {
        Assert.False(DictationOverlayPresentation.ShowBuiltInPartialPreview(
            "confirmed live text",
            externalLivePreviewActive: false,
            liveTranscriptionEnabled: false,
            indicatorStyle: IndicatorStyle.StatusIsland));
    }

    [Fact]
    public void BuiltInPartialPreview_ShowsForEdgeDock()
    {
        Assert.True(DictationOverlayPresentation.ShowBuiltInPartialPreview(
            "confirmed live text",
            externalLivePreviewActive: false,
            liveTranscriptionEnabled: true,
            indicatorStyle: IndicatorStyle.EdgeDock));
    }

    [Fact]
    public void BuiltInPartialPreview_HidesForCompactBadge()
    {
        Assert.False(DictationOverlayPresentation.ShowBuiltInPartialPreview(
            "confirmed live text",
            externalLivePreviewActive: false,
            liveTranscriptionEnabled: true,
            indicatorStyle: IndicatorStyle.CompactBadge));
    }
}
