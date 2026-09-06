using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryModelDisplayTests
{
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
        var transcript = new PrototypeTranscript(HistoryEntryAdapter.FromRecord(record), "Today");
        Assert.Equal(expected, transcript.ModelLabel);
        Assert.Equal("Model: " + expected, transcript.ModelMetadata);
        Assert.Equal(model, record.ModelUsed);
    }
}
