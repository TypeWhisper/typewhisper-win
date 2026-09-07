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

    [Theory]
    [InlineData("dictation", PrototypeHistoryEntryKind.Dictation)]
    [InlineData("recording", PrototypeHistoryEntryKind.Recording)]
    [InlineData("file", PrototypeHistoryEntryKind.ImportedFile)]
    [InlineData("future-source", PrototypeHistoryEntryKind.Unknown)]
    [InlineData("Dictation", PrototypeHistoryEntryKind.Unknown)]
    [InlineData(" dictation ", PrototypeHistoryEntryKind.Unknown)]
    [InlineData(null, PrototypeHistoryEntryKind.Unknown)]
    [InlineData("", PrototypeHistoryEntryKind.Unknown)]
    public void OnlyExplicitKnownSourceValuesDetermineKind(string? source, PrototypeHistoryEntryKind expected)
    {
        var original = Record("source", source);
        var loaded = JsonSerializer.Deserialize<TranscriptionRecord>(JsonSerializer.Serialize(original))!;
        Assert.Equal(source, loaded.SourceKind);
        var projected = HistoryEntryAdapter.FromRecord(loaded);
        projected.Validate();
        Assert.Equal(expected, projected.Content.Kind);
        Assert.Equal(string.IsNullOrWhiteSpace(source) ? "legacy" : source, projected.Content.SourceRaw);
        Assert.Equal("unknown", projected.Content.Origin.DeviceId);
    }

    [Fact]
    public void OldRecordAndUnknownExtraJsonFieldsRemainReadableWithoutInventingSource()
    {
        const string json = """
            {"Id":"old","Timestamp":"2026-09-01T12:00:00Z","RawText":"Original","FinalText":"Final",
             "AppName":"Notepad","AudioFileName":"dictation.wav","FutureMetadata":{"anything":true}}
            """;
        var record = JsonSerializer.Deserialize<TranscriptionRecord>(json)!;
        Assert.Null(record.SourceKind);
        Assert.Equal(PrototypeHistoryEntryKind.Unknown, HistoryEntryAdapter.FromRecord(record).Content.Kind);
        Assert.DoesNotContain("SourceKind", JsonSerializer.Serialize(record));
    }

    [Fact]
    public void DictationsFilterIncludesExplicitDictationAndExcludesLegacyAndOtherKinds()
    {
        var records = new[] { Record("dictated", "dictation"), Record("recorded", "recording"),
            Record("imported", "file"), Record("legacy", null), Record("future", "future-source") };
        var store = new PrototypeHistoryStore(records.Select(HistoryEntryAdapter.FromRecord));
        Assert.Equal(5, store.Query().Count);
        var dictation = Assert.Single(store.Query(kind: PrototypeHistoryEntryKind.Dictation));
        Assert.Equal("dictated", dictation.PersistedRecordId);
        Assert.Equal(2, store.Query(kind: PrototypeHistoryEntryKind.Unknown).Count);
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
