using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistorySourceKindTests
{
    private static TranscriptionRecord Record(string id, string? kind) => new()
    {
        Id = id, Timestamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        RawText = "Original", FinalText = "Final", SourceKind = kind,
        EngineUsed = "sherpa-onnx", ModelUsed = "parakeet-tdt-0.6b",
        AppName = "Notepad", AudioFileName = "dictation.wav"
    };

    [Fact]
    public void OldRecordAndUnknownExtraJsonFieldsRemainReadableWithoutInventingSource()
    {
        const string json = """
            {"Id":"old","Timestamp":"2026-09-01T12:00:00Z","RawText":"Original","FinalText":"Final",
             "AppName":"Notepad","AudioFileName":"dictation.wav","FutureMetadata":{"anything":true}}
            """;
        var record = JsonSerializer.Deserialize<TranscriptionRecord>(json)!;
        Assert.Null(record.SourceKind);
        Assert.DoesNotContain("SourceKind", JsonSerializer.Serialize(record));
    }

    [Fact]
    public async Task HistoryServicePersistsExplicitAndUnfamiliarSourceKinds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typewhisper-history-source-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.json");
        try
        {
            var history = new HistoryService(path) { ThrowOnLoadFailure = true };
            Assert.True(history.TryAddRecord(Record("dictated", "dictation")));
            Assert.True(history.TryAddRecord(Record("future", "future-source")));
            var reloaded = new HistoryService(path) { ThrowOnLoadFailure = true };
            await reloaded.EnsureLoadedAsync();
            Assert.Equal("dictation", reloaded.Records.Single(record => record.Id == "dictated").SourceKind);
            Assert.Equal("future-source", reloaded.Records.Single(record => record.Id == "future").SourceKind);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
