using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowActionTests
{
    [Fact]
    public void ActionDestinationSurvivesDraftAndAutomaticSnapshot()
    {
        var workflow = new Workflow { Id = "linear", Name = "Linear", Template = WorkflowTemplate.Dictation,
            Trigger = WorkflowTrigger.Global(), Output = new() { TargetActionPluginId = "com.typewhisper.linear" } };
        var draft = WorkflowDraft.FromStored(workflow);
        Assert.True(draft.IsEditable);
        Assert.Equal(workflow.Output, draft.ToStored().Output);
        var snapshot = AutomaticWorkflowSnapshot.Select([workflow], "notepad");
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Error);
        Assert.Equal("com.typewhisper.linear", snapshot.TargetActionPluginId);
        Assert.Null((draft with { TargetActionPluginId = null }).ToStored().Output.TargetActionPluginId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActionRunsOnceInsteadOfPastingAndFailureKeepsText(bool success)
    {
        var record = new TranscriptionRecord { Id = "test", Timestamp = DateTime.UtcNow, FinalText = "Processed issue", RawText = "Original dictation" };
        var preferences = new DictationOutputPreferences { SaveToHistory = false };
        var calls = 0;
        var result = await new DictationOutputDelivery(new Mock<IHistoryService>(MockBehavior.Strict).Object)
            .DeliverAsync(record, preferences, () => preferences, () => throw new Exception("Must not paste"),
                action: _ => { calls++; return Task.FromResult(new WorkflowActionResult(success, "Issue outcome")); });
        Assert.Equal(1, calls);
        Assert.Equal(!success, result.NeedsReview);
        Assert.Equal(!success, result.Failed);
        Assert.Equal(record, result.Record);
        Assert.True(result.ActionAttempted);
        Assert.Equal(success ? DictationReviewReason.None : DictationReviewReason.ActionFailed, result.ReviewReason);
    }

    [Fact]
    public async Task HistoryFailureDoesNotBlockOrRepeatTheSelectedAction()
    {
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.EnsureLoadedAsync()).ThrowsAsync(new IOException());
        var calls = 0;
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(
            new() { Id = "test", Timestamp = DateTime.UtcNow, RawText = "text", FinalText = "text" },
            new(), () => new(), () => throw new Exception("Must not paste"),
            action: _ => { calls++; return Task.FromResult(new WorkflowActionResult(true, "Created")); });
        Assert.Equal(1, calls);
        Assert.True(result.ActionAttempted);
        Assert.False(result.NeedsReview);
        Assert.False(result.Saved);
        Assert.True(result.Failed);
        Assert.NotNull(result.StorageWarning);
        Assert.Equal(DictationReviewReason.None, result.ReviewReason);
    }

    [Fact]
    public async Task FailedProcessingNeverSendsAnAction()
    {
        var preferences = new DictationOutputPreferences { SaveToHistory = false };
        var result = await new DictationOutputDelivery(new Mock<IHistoryService>(MockBehavior.Strict).Object)
            .DeliverAsync(new() { Id = "test", Timestamp = DateTime.UtcNow, RawText = "text", FinalText = "text", Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed }, preferences,
                () => preferences, () => throw new Exception("Must not paste"),
                action: _ => throw new Exception("Must not send"));
        Assert.True(result.NeedsReview);
        Assert.False(result.ActionAttempted);
        Assert.Equal(DictationReviewReason.ProcessingFailed, result.ReviewReason);
    }

    [Fact]
    public async Task ConfirmedActionSurvivesLateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var preferences = new DictationOutputPreferences { SaveToHistory = false };
        var result = await new DictationOutputDelivery(new Mock<IHistoryService>(MockBehavior.Strict).Object)
            .DeliverAsync(new() { Id = "test", Timestamp = DateTime.UtcNow, RawText = "text", FinalText = "text" }, preferences, () => preferences, () => throw new Exception("Must not paste"), cancellation.Token,
                action: _ => { cancellation.Cancel(); return Task.FromResult(new WorkflowActionResult(true, "Created")); });
        Assert.False(result.NeedsReview);
        Assert.Equal("Created Not saved to History.", result.Message);
    }
}
