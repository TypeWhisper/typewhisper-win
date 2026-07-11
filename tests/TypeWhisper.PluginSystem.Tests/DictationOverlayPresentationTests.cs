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
    public void ClearFeedbackAction_ResetsLearnedFeedbackAutoHideDuration()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var clearBlock = TestFile.ExtractBlock(source, "private void ClearFeedbackAction()", 600);

        var resetIndex = clearBlock.IndexOf("_feedbackAutoHideMilliseconds = 2000;", StringComparison.Ordinal);
        var earlyReturnIndex = clearBlock.IndexOf(
            "if (_pendingLearnedCorrections.Count == 0 && FeedbackActionText is null)",
            StringComparison.Ordinal);

        Assert.True(resetIndex >= 0, "Expected ClearFeedbackAction to restore the transient feedback timeout.");
        Assert.True(earlyReturnIndex >= 0, "Expected ClearFeedbackAction to keep the empty-state guard.");
        Assert.True(resetIndex < earlyReturnIndex, "Expected the timeout reset to run even when no action is pending.");
    }

    [Fact]
    public void MissingModel_UsesVisibleLoggedFeedbackPath()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var helper = TestFile.ExtractBlock(
            source,
            "private void ApplyModelUnavailableFeedback(string? modelId, Exception? error = null)",
            900);
        var startRecording = TestFile.ExtractBlock(source, "private async Task StartRecording()", 4200);

        Assert.Contains("_errorLog.AddEntry(diagnostic, ErrorCategory.Transcription);", helper, StringComparison.Ordinal);
        Assert.Contains("ApplyTransientIdleFeedback(feedback, feedbackIsError: true);", helper, StringComparison.Ordinal);
        Assert.Contains("ApplyModelUnavailableFeedback(desiredModelId);", startRecording, StringComparison.Ordinal);
        Assert.Contains("ApplyModelUnavailableFeedback(desiredModelId, ex);", startRecording, StringComparison.Ordinal);
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
