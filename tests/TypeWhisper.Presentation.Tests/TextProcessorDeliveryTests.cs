using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Xunit;

public sealed class TextProcessorDeliveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessorFailureAlwaysReviewsAndRespectsHistoryPrivacy(bool save)
    {
        var pipeline = await DictationTextPipeline.ProcessAsync("two", new(), "en", textProcessors:
            [new("failed.plugin", "1.2", 250, (_, _) => throw new IOException("private provider message"))]);
        var record = new TranscriptionRecord { Id = "test", Timestamp = DateTime.UtcNow, RawText = "two",
            FinalText = pipeline.Text, TextProcessors = pipeline.TextProcessors,
            Status = TranscriptionRecordStatus.TextProcessorFailed };
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        if (save)
        {
            history.Setup(item => item.EnsureLoadedAsync()).Returns(Task.CompletedTask);
            history.Setup(item => item.TryAddRecord(record)).Returns(true);
        }
        var prefs = new DictationOutputPreferences { SaveToHistory = save, AutoPaste = true };
        var pasted = false;
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(record, prefs, () => prefs,
            () => { pasted = true; return Task.FromResult(true); });
        Assert.False(pasted);
        Assert.True(result.NeedsReview);
        Assert.Equal(save, result.Saved);
        Assert.Equal("2", result.Record.FinalText);
        history.Verify(item => item.TryAddRecord(It.IsAny<TranscriptionRecord>()), save ? Times.Once() : Times.Never());
        var json = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("private provider message", json);
        var restored = JsonSerializer.Deserialize<TranscriptionRecord>(json)!;
        Assert.Equal(record.TextProcessors!.ToArray(), restored.TextProcessors!.ToArray());
        Assert.Equal(TranscriptionRecordStatus.TextProcessorFailed, restored.Status);
    }
}
