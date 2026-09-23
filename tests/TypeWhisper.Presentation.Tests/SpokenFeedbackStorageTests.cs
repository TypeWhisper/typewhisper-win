using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SpokenFeedbackStorageTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task StorageWarningDoesNotChangeDeliveryAdmission(bool audio, bool action, bool delivered)
    {
        var record = new TranscriptionRecord
        {
            Id = "speech-storage", Timestamp = DateTime.UtcNow, SourceKind = "dictation",
            RawText = "Test dictation", FinalText = "Test dictation"
        };
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        if (audio)
            history.Setup(h => h.TryAddRecordWithAudio(record, It.IsAny<float[]>(), 16000,
                It.IsAny<Func<bool>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<bool>>()))
                .Returns(new HistoryAudioSaveResult(record, true, "Audio could not be retained."));
        else
            history.Setup(h => h.TryAddRecord(record)).Returns(false);
        var preferences = new DictationOutputPreferences { SaveHistoryAudio = audio };
        var outcome = await new DictationOutputDelivery(history.Object).DeliverAsync(record,
            preferences, () => preferences, () => Task.FromResult(delivered), samples: [0.1f],
            action: action ? _ => Task.FromResult(new WorkflowActionResult(delivered, "Action outcome")) : null);

        Assert.True(outcome.Failed); // Keep recovery/storage error handling intact.
        Assert.NotNull(outcome.StorageWarning);
        Assert.Equal(delivered, SpokenFeedbackPolicy.ShouldSpeakAutomatically(true, true, outcome));
        Assert.False(SpokenFeedbackPolicy.ShouldSpeakAutomatically(false, true, outcome));
        Assert.False(SpokenFeedbackPolicy.ShouldSpeakAutomatically(true, false, outcome));
    }
}
