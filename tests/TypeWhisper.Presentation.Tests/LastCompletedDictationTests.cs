using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LastCompletedDictationTests
{
    private static TranscriptionRecord Record(string text = "Final corrected text") => new()
    {
        Id = "recording-id", Timestamp = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc),
        RawText = "raw must not be copied", FinalText = text, SourceKind = "dictation",
        Language = "de", EngineUsed = "engine", ModelUsed = "model"
    };
    private static DictationOutputResult Result(TranscriptionRecord? record = null) => new(record ?? Record(), false, true, "Review");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealDeliveryPublishesFinalTextRegardlessOfHistoryAndReviewPreference(bool save, bool paste)
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var record = Record();
        if (save)
        {
            history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
            history.Setup(h => h.TryAddRecord(record)).Returns(true);
        }
        var preferences = new DictationOutputPreferences { SaveToHistory = save, AutoPaste = paste };
        var outcome = await new DictationOutputDelivery(history.Object).DeliverAsync(record, preferences, () => preferences,
            () => Task.FromResult(true));
        var store = new LastCompletedDictationStore();
        Assert.True(store.TryPublish(outcome));
        Assert.Equal(record.FinalText, store.Current?.Text); Assert.Equal(record.Id, store.Current?.Id);
        Assert.Equal(record.Timestamp, store.Current?.RecordedAt);
        Assert.Equal("de", store.Current?.Language); Assert.Equal("engine", store.Current?.Engine); Assert.Equal("model", store.Current?.Model);
        Assert.Equal(DateTimeKind.Utc, store.Current!.CompletedAt.Kind);
        history.Verify(h => h.EnsureLoadedAsync(), save ? Times.Once() : Times.Never());
        history.Verify(h => h.TryAddRecord(record), save ? Times.Once() : Times.Never());
        history.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryOrPasteFailureStillRetainsCorrectFinalText(bool historyFails)
    {
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        history.Setup(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>())).Returns(!historyFails);
        var preferences = new DictationOutputPreferences { SaveToHistory = true, AutoPaste = true };
        var outcome = await new DictationOutputDelivery(history.Object).DeliverAsync(Record(), preferences, () => preferences,
            () => Task.FromResult(false));
        Assert.True(outcome.Failed); Assert.True(outcome.NeedsReview);
        var store = new LastCompletedDictationStore();
        Assert.True(store.TryPublish(outcome)); Assert.Equal(Record().FinalText, store.Current?.Text);
    }

    [Theory]
    [InlineData(TranscriptionRecordStatus.WorkflowPostProcessingFailed)]
    [InlineData(TranscriptionRecordStatus.TextProcessorFailed)]
    [InlineData((TranscriptionRecordStatus)99)]
    public void FailedPipelineNeverReplacesEarlierSuccessfulResult(TranscriptionRecordStatus status)
    {
        var store = new LastCompletedDictationStore(); Assert.True(store.TryPublish(Result())); var previous = store.Current;
        Assert.False(store.TryPublish(Result(Record("failed-stage text") with { Status = status })));
        Assert.Same(previous, store.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file")]
    [InlineData("recording")]
    [InlineData("recovery")]
    [InlineData("workflow")]
    public void OtherOrUnknownSourcesNeverReplaceDictation(string? source)
    {
        var store = new LastCompletedDictationStore(); Assert.True(store.TryPublish(Result())); var previous = store.Current;
        Assert.False(store.TryPublish(Result(Record("other source") with { SourceKind = source })));
        Assert.Same(previous, store.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void EmptyFinalTextDoesNotFallBackToRawText(string text)
    {
        var store = new LastCompletedDictationStore();
        Assert.False(store.TryPublish(Result(Record(text)))); Assert.Null(store.Current);
    }

    [Fact]
    public void Utf8TextAndMetadataLimitsRejectWithoutTruncatingOrReplacing()
    {
        var store = new LastCompletedDictationStore();
        var exact = new string('ä', LastCompletedDictationStore.MaximumTextBytes / 2);
        Assert.True(store.TryPublish(Result(Record(exact)))); var previous = store.Current;
        Assert.Equal(exact, previous!.Text);
        Assert.False(store.TryPublish(Result(Record(exact + "a"))));
        Assert.False(store.TryPublish(Result(Record() with { ModelUsed = new string('ä', LastCompletedDictationStore.MaximumMetadataBytes / 2 + 1) })));
        Assert.False(store.TryPublish(Result(Record() with { Id = "" })));
        Assert.Same(previous, store.Current);
    }

    [Fact]
    public void SnapshotDoesNotExposeMutableRecordMetadataOrChangeEarlierReaders()
    {
        var provenance = new List<TextProcessorProvenance>();
        var record = Record() with { TextProcessors = provenance };
        var store = new LastCompletedDictationStore(); Assert.True(store.TryPublish(Result(record)));
        var first = store.Current;
        record = record with { FinalText = "edited independently", Language = "en" };
        provenance.Clear();
        Assert.Equal("Final corrected text", first?.Text); Assert.Equal("de", first?.Language);
        Assert.True(store.TryPublish(Result(record)));
        Assert.Equal("edited independently", store.Current?.Text);
        Assert.Equal("Final corrected text", first?.Text);
    }

    [Fact]
    public async Task CloseWhileDeliveryIsPendingPreventsLatePublicationAndClearsPreviousResult()
    {
        var store = new LastCompletedDictationStore(); Assert.True(store.TryPublish(Result()));
        var history = new Mock<IHistoryService>();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(loaded.Task);
        history.Setup(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>())).Returns(true);
        var preferences = new DictationOutputPreferences { SaveToHistory = true, AutoPaste = false };
        var pending = new DictationOutputDelivery(history.Object).DeliverAsync(Record("late"), preferences, () => preferences, () => Task.FromResult(false));
        Assert.False(pending.IsCompleted);
        store.Close(); store.Close(); Assert.Null(store.Current);
        loaded.SetResult(); var result = await pending;
        Assert.False(store.TryPublish(result)); Assert.Null(store.Current);
    }

    [Fact]
    public void CancellationAfterCompletedDeliveryPreventsPublicationWithoutDroppingPreviousText()
    {
        var store = new LastCompletedDictationStore(); Assert.True(store.TryPublish(Result())); var previous = store.Current;
        var result = Result(Record("late"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.False(store.TryPublish(result, cancellation.Token)); Assert.Same(previous, store.Current);
        store.Close(); Assert.Null(store.Current);
    }
}
