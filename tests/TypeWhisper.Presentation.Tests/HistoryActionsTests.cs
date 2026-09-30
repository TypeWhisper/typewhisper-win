using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryActionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-history-actions-" + Guid.NewGuid());
    private string HistoryPath => Path.Combine(_directory, "history.json");

    private static TranscriptionRecord Record => new()
    {
        Id = "opaque-existing-id", Timestamp = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc),
        RawText = "original dictated text", FinalText = "initial final text", AppName = "Editor",
        AppProcessName = "editor", Language = "de", ModelUsed = "model", TranscriptionTaskUsed = "transcribe"
    };

    private HistoryService Create()
    {
        Directory.CreateDirectory(_directory);
        var service = new HistoryService(HistoryPath) { ThrowOnLoadFailure = true };
        Assert.True(service.TryAddRecord(Record));
        return service;
    }

    [Fact]
    public async Task EditAndDeletePersistAcrossRestartAndPreserveProvenance()
    {
        var service = Create();
        var updated = await new HistoryActions(service).EditAsync(Record.Id, "corrected transcript");
        // Editing stamps the content so History sync can pick the newest text.
        Assert.NotNull(updated!.ContentUpdatedAt);
        Assert.Equal(Record with { FinalText = "corrected transcript", ContentUpdatedAt = updated.ContentUpdatedAt }, updated);
        var restarted = new HistoryService(HistoryPath) { ThrowOnLoadFailure = true };
        Assert.Equal(updated, Assert.Single(await new HistoryReader(restarted).ReadAsync("corrected")));
        Assert.True(await new HistoryActions(restarted).DeleteAsync(Record.Id));
        Assert.Empty(await new HistoryReader(new HistoryService(HistoryPath)).ReadAsync());
    }

    [Fact]
    public async Task InboxCompletionOnlyChangesInboxEntriesAndPersists()
    {
        var service = Create();
        var actions = new HistoryActions(service);
        // A local entry was never in the Inbox, so neither action applies to it.
        Assert.Equal(0, await actions.SetInboxCompletedAsync([Record.Id], completed: true));
        Assert.Null(service.Records.Single().InboxState);
        Assert.True(service.TryReplaceRecord(Record with { InboxState = HistoryWorkspace.InboxOpen }));
        var at = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await actions.SetInboxCompletedAsync([Record.Id], completed: true, at));
        var restarted = new HistoryService(HistoryPath) { ThrowOnLoadFailure = true };
        var completed = Assert.Single(await new HistoryReader(restarted).ReadAsync());
        Assert.Equal((HistoryWorkspace.InboxCompleted, at), (completed.InboxState, completed.InboxCompletedAt));
        Assert.Equal(1, await new HistoryActions(restarted).SetInboxCompletedAsync([Record.Id], completed: false));
        Assert.Equal(HistoryWorkspace.InboxOpen, restarted.Records.Single().InboxState);
        Assert.Null(restarted.Records.Single().InboxCompletedAt);
    }

    [Fact]
    public async Task FailedDiskWritesDoNotChangeViewSnapshotOrReportSuccess()
    {
        var service = Create();
        var bytes = await File.ReadAllBytesAsync(HistoryPath);
        File.Move(HistoryPath, HistoryPath + ".saved");
        Directory.CreateDirectory(HistoryPath);
        var actions = new HistoryActions(service);
        Assert.Null(await actions.EditAsync(Record.Id, "must not appear"));
        Assert.False(await actions.DeleteAsync(Record.Id));
        Assert.Equal(Record, Assert.Single(service.Records));
        Directory.Delete(HistoryPath);
        File.Move(HistoryPath + ".saved", HistoryPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(HistoryPath));
        Assert.Equal(Record, Assert.Single(await new HistoryReader(new HistoryService(HistoryPath)).ReadAsync()));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".csv")]
    [InlineData(".json")]
    public async Task ExportUsesPersistedEditsWithoutMutatingHistory(string extension)
    {
        var service = Create();
        var actions = new HistoryActions(service);
        await actions.EditAsync(Record.Id, "exported correction");
        var bytes = await File.ReadAllBytesAsync(HistoryPath);
        Assert.Contains("exported correction", await actions.ExportAsync(Record.Id, extension));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(HistoryPath));
    }

    [Fact]
    public async Task FileExportReplacesExistingTargetAndCleansTemporaryFiles()
    {
        var actions = new HistoryActions(Create());
        var target = Path.Combine(_directory, "export.txt");
        await File.WriteAllTextAsync(target, "previous export");
        await actions.ExportFileAsync(Record.Id, target);
        Assert.Contains(Record.FinalText, await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_directory, ".typewhisper-export-*.tmp"));
    }

    [Fact]
    public async Task FailedExportPreservesExistingDestinationAndCleansTemporaryFiles()
    {
        var actions = new HistoryActions(Create());
        var target = Path.Combine(_directory, "export.txt");
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "existing.txt");
        await File.WriteAllTextAsync(existing, "preserve me");
        var failure = await Xunit.Record.ExceptionAsync(() => actions.ExportFileAsync(Record.Id, target));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(existing));
        Assert.Empty(Directory.GetFiles(_directory, ".typewhisper-export-*.tmp"));

        var file = Path.Combine(_directory, "existing.txt");
        await File.WriteAllTextAsync(file, "previous export");
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExportFileAsync("missing", file));
        Assert.Equal("previous export", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task BlankEditsAndMissingRecordsCannotOverwriteExistingEntry()
    {
        var service = Create();
        var actions = new HistoryActions(service);
        await Assert.ThrowsAsync<ArgumentException>(() => actions.EditAsync(Record.Id, "  "));
        Assert.Null(await actions.EditAsync("missing", "text"));
        Assert.Equal(Record, Assert.Single(service.Records));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
