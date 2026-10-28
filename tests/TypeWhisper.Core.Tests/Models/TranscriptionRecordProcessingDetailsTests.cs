using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class TranscriptionRecordProcessingDetailsTests
{
    private static TranscriptionRecord Record() => new()
    {
        Id = "entry", Timestamp = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc), RawText = "raw", FinalText = "Final."
    };

    [Fact]
    public void ProcessingDetails_RoundTripThroughHistoryJson()
    {
        var record = Record() with { TranscriptionSeconds = 1.25, LlmProvider = "OpenAI", LlmModel = "GPT-4o mini", LlmSeconds = 2.5 };

        var restored = JsonSerializer.Deserialize<TranscriptionRecord>(JsonSerializer.Serialize(record))!;

        Assert.Equal(1.25, restored.TranscriptionSeconds);
        Assert.Equal("OpenAI", restored.LlmProvider);
        Assert.Equal("GPT-4o mini", restored.LlmModel);
        Assert.Equal(2.5, restored.LlmSeconds);
    }

    [Fact]
    public void MissingProcessingDetails_AreNotWrittenAndReadAsMissing()
    {
        var json = JsonSerializer.Serialize(Record());

        Assert.DoesNotContain(nameof(TranscriptionRecord.TranscriptionSeconds), json);
        Assert.DoesNotContain(nameof(TranscriptionRecord.LlmProvider), json);
        Assert.DoesNotContain(nameof(TranscriptionRecord.LlmModel), json);
        Assert.DoesNotContain(nameof(TranscriptionRecord.LlmSeconds), json);
        var restored = JsonSerializer.Deserialize<TranscriptionRecord>(json)!;
        Assert.Null(restored.TranscriptionSeconds);
        Assert.Null(restored.LlmProvider);
        Assert.Null(restored.LlmSeconds);
    }
}
