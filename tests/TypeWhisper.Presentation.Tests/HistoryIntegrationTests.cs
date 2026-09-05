using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryIntegrationTests
{
    [Fact]
    public void ProjectionPreservesTextAndDoesNotInventOriginOrKind()
    {
        var record = new TranscriptionRecord
        {
            Id = "opaque-record-id", Timestamp = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
            RawText = "Original", FinalText = "", AudioFileName = "recording.wav", EngineUsed = "parakeet"
        };
        var projected = HistoryEntryAdapter.FromRecord(record);
        projected.Validate();
        Assert.Equal("Original", projected.Content.Transcript!.FinalText);
        Assert.Equal(PrototypeHistoryEntryKind.Unknown, projected.Content.Kind);
        Assert.Equal("unknown", projected.Content.Origin.DeviceId);
        Assert.Equal("parakeet", projected.Content.EngineName);
        Assert.Equal(projected.RecordId, HistoryEntryAdapter.FromRecord(record).RecordId);
        Assert.False(projected.LocalState.IsSample);
        Assert.Equal("opaque-record-id", record.Id);
    }

    [Fact]
    public async Task StrictMissingHistoryIsEmptyWithoutCreatingFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "typewhisper-history-test-" + Guid.NewGuid(), "history.json");
        var reader = new HistoryReader(new HistoryService(path) { ThrowOnLoadFailure = true });
        Assert.Empty(await reader.ReadAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task StrictCorruptHistoryFailsWithoutRewritingAndCanRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), "typewhisper-history-test-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "not json");
            var reader = new HistoryReader(new HistoryService(path) { ThrowOnLoadFailure = true });
            await Assert.ThrowsAsync<JsonException>(() => reader.ReadAsync());
            Assert.Equal("not json", await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, "[]");
            Assert.Empty(await reader.ReadAsync());
        }
        finally { File.Delete(path); }
    }
}
