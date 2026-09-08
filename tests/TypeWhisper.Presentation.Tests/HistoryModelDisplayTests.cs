using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryModelDisplayTests
{
    [Fact]
    public void FailedWorkflowKeepsStoredIdentityAndOriginalTranscriptForDetails()
    {
        var record = new TranscriptionRecord
        {
            Id = "failed-workflow", Timestamp = DateTime.UtcNow, RawText = "Original dictation", FinalText = "",
            WorkflowId = "removed-rule", ProfileName = "Saved workflow name",
            Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed,
            WorkflowFailureMessage = "The selected provider is unavailable."
        };
        var entry = HistoryEntryAdapter.FromRecord(record);
        Assert.Equal("removed-rule", entry.Content.WorkflowId);
        Assert.Equal("Saved workflow name", entry.Content.WorkflowName);
        Assert.Equal(HistoryProcessingState.Failed, entry.Content.ProcessingState);
        Assert.Equal(record.WorkflowFailureMessage, entry.Content.FailureMessage);
        Assert.Equal("Original dictation", entry.Content.Transcript!.FinalText);
        Assert.Equal("Original dictation", entry.Content.Transcript.RawText);
    }

    [Theory]
    [InlineData("sherpa-onnx", "parakeet-tdt-0.6b", "Local · Parakeet TDT 0.6B")]
    [InlineData("sherpa-onnx", "canary-180m-flash", "Local · Canary 180M Flash")]
    [InlineData("groq", "whisper-large-v3", "Groq · Whisper Large V3")]
    [InlineData("groq", "whisper-large-v3-turbo", "Groq · Whisper Large V3 Turbo")]
    [InlineData("future-provider", "future-model-42", "future-provider · future-model-42")]
    [InlineData(null, "future-model", "future-model")]
    [InlineData("groq", null, "Model not recorded")]
    public void UsesStoredProviderAndModelWithoutAssumingTheCurrentModel(string? provider, string? model, string expected)
    {
        var record = new TranscriptionRecord { Id = "historical-entry", Timestamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), RawText = "dictated", FinalText = "Dictated.", EngineUsed = provider ?? "", ModelUsed = model };
        var transcript = new Transcript(HistoryEntryAdapter.FromRecord(record), "Today");
        Assert.Equal(expected, transcript.ModelLabel);
        Assert.Equal("Model: " + expected, transcript.ModelMetadata);
        Assert.Equal(model, record.ModelUsed);
    }
}
