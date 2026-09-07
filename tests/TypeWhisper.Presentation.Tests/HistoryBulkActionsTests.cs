using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryBulkActionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-history-bulk-" + Guid.NewGuid());
    private string HistoryPath => Path.Combine(_directory, "history.json");
    private static TranscriptionRecord Record(string id) => new()
    {
        Id = id, RawText = "raw " + id, FinalText = "Text ä " + id, Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow, DurationSeconds = 2
    };

    private HistoryService Create()
    {
        Directory.CreateDirectory(_directory);
        var history = new HistoryService(HistoryPath, _directory) { ThrowOnLoadFailure = true };
        Assert.True(history.TryAddRecord(Record("one")));
        Assert.True(history.TryAddRecord(Record("two")));
        return history;
    }

    [Fact]
    public async Task ConfirmedClearSnapshotPreservesLaterEntriesAcrossRestart()
    {
        var history = Create();
        var actions = new HistoryActions(history);
        var confirmed = await actions.SnapshotIdsAsync();
        Assert.Equal(2, confirmed.Length);
        Assert.True(history.TryAddRecord(Record("added-during-dialog")));
        Assert.True(await actions.DeleteAsync(confirmed));
        Assert.Equal("added-during-dialog", Assert.Single(new HistoryService(HistoryPath).Records).Id);
    }

    [Fact]
    public async Task AtomicDeletePreservesConcurrentAddAndPublishesOneDeletionEvent()
    {
        var history = Create();
        var events = 0;
        history.RecordsChanged += () => Interlocked.Increment(ref events);
        await Task.WhenAll(Task.Run(() => Assert.True(history.TryDeleteRecords(["one", "two"]))),
            Task.Run(() => Assert.True(history.TryAddRecord(Record("concurrent")))));
        Assert.Equal(2, events);
        Assert.Equal("concurrent", Assert.Single(history.Records).Id);
        Assert.Equal("concurrent", Assert.Single(new HistoryService(HistoryPath).Records).Id);
        Assert.Equal(1, history.TotalRecords);
    }

    [Fact]
    public async Task FailedBulkWriteKeepsAllRecordsStatsAndAudio()
    {
        var history = Create();
        Assert.True(history.TryReplaceRecord(Record("one") with { AudioFileName = "one.wav" }));
        var audio = Path.Combine(_directory, "one.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        var before = File.ReadAllBytes(HistoryPath);
        File.Move(HistoryPath, HistoryPath + ".saved");
        Directory.CreateDirectory(HistoryPath);
        var events = 0;
        history.RecordsChanged += () => events++;
        Assert.False(await new HistoryActions(history).DeleteAsync(new[] { "one", "two" }));
        Assert.Equal(2, history.Records.Count);
        Assert.Equal(2, history.TotalRecords);
        Assert.Equal(0, events);
        Assert.True(File.Exists(audio));
        Directory.Delete(HistoryPath);
        File.Move(HistoryPath + ".saved", HistoryPath);
        Assert.Equal(before, File.ReadAllBytes(HistoryPath));
        Assert.Equal(2, new HistoryService(HistoryPath).Records.Count);
    }

    [Fact]
    public void BulkDeleteRemovesOnlyUnreferencedSelectedAudio()
    {
        var history = Create();
        Assert.True(history.TryReplaceRecord(Record("one") with { AudioFileName = "shared.wav" }));
        Assert.True(history.TryReplaceRecord(Record("two") with { AudioFileName = "shared.wav" }));
        var audio = Path.Combine(_directory, "shared.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        Assert.True(history.TryDeleteRecords(["one", "one", "missing"]));
        Assert.True(File.Exists(audio));
        Assert.True(history.TryDeleteRecords(["two"]));
        Assert.False(File.Exists(audio));
        Assert.Empty(new HistoryService(HistoryPath).Records);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".csv")]
    [InlineData(".json")]
    public async Task ExportIncludesOnlySelectedPersistedRecords(string extension)
    {
        var history = Create();
        Assert.True(history.TryAddRecord(Record("unselected")));
        var text = await new HistoryActions(history).ExportAsync(new[] { "one", "two", "one" }, extension);
        Assert.Contains("one", text);
        Assert.Contains("two", text);
        Assert.DoesNotContain("unselected", text);
        Assert.Equal(3, history.Records.Count);
    }

    [Fact]
    public async Task MissingSelectionDoesNotPartiallyOverwriteExport()
    {
        var history = Create();
        var target = Path.Combine(_directory, "selection.txt");
        File.WriteAllText(target, "existing destination");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoryActions(history)
            .ExportFileAsync(new[] { "one", "missing" }, target));
        Assert.Equal("existing destination", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_directory, ".typewhisper-export-*.tmp"));
    }

    [Fact]
    public async Task ExportWritesUtf8WithoutBomAndCleansTemporaryFile()
    {
        var history = Create();
        var target = Path.Combine(_directory, "selection.json");
        await new HistoryActions(history).ExportFileAsync(new[] { "one", "two" }, target);
        var bytes = File.ReadAllBytes(target);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Empty(Directory.GetFiles(_directory, ".typewhisper-export-*.tmp"));
    }

    [Fact]
    public async Task UnimplementedBulkServiceDoesNotFallBackToPartialIndividualDeletes()
    {
        var service = new Mock<IHistoryService>();
        service.Setup(history => history.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        service.Setup(history => history.TryDeleteRecords(It.IsAny<IReadOnlyCollection<string>>())).Returns(false);
        Assert.False(await new HistoryActions(service.Object).DeleteAsync(new[] { "one", "two" }));
        service.Verify(history => history.DeleteRecord(It.IsAny<string>()), Times.Never);
        service.Verify(history => history.ClearAll(), Times.Never);
        service.Verify(history => history.TryReplaceAll(It.IsAny<IReadOnlyList<TranscriptionRecord>>()), Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
