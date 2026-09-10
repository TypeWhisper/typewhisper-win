using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class LegacyDailyProfileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-daily-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "legacy");
    private string Destination => Path.Combine(_root, "daily");

    private string Seed()
    {
        var data = Path.Combine(Source, "Data");
        Directory.CreateDirectory(data);
        new DictionaryService(Path.Combine(data, "dictionary.json")).AddEntry(new DictionaryEntry
        { Id = "word", Original = "Wörterbuch", EntryType = DictionaryEntryType.Term });
        new SnippetService(Path.Combine(data, "snippets.json")).AddSnippet(new Snippet
        { Id = "snippet", Trigger = "hello", Replacement = "Grüße" });
        new WorkflowService(Path.Combine(data, "workflows.json")).AddWorkflow(new Workflow
        { Id = "workflow", Name = "Summary", Template = WorkflowTemplate.Summary, Trigger = WorkflowTrigger.Manual() });
        new HistoryService(Path.Combine(data, "history.json")).AddRecord(new TranscriptionRecord
        { Id = "history", RawText = "raw", FinalText = "final", Timestamp = DateTime.UtcNow, AudioFileName = "legacy.wav" });
        File.WriteAllText(Path.Combine(Source, "settings.json"), "private settings");
        Directory.CreateDirectory(Path.Combine(Source, "Plugins"));
        File.WriteAllText(Path.Combine(Source, "Plugins", "old.dll"), "legacy binary");
        return data;
    }

    [Fact]
    public async Task CopiesCategoriesWithoutTouchingLegacyOrImportingDeviceData()
    {
        Seed();
        var originals = Directory.GetFiles(Source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.Equal("Wörterbuch", Assert.Single(new DictionaryService(Path.Combine(Destination, "dictionary.json")).Entries).Original);
        Assert.Equal("Grüße", Assert.Single(new SnippetService(Path.Combine(Destination, "snippets.json")).Snippets).Replacement);
        Assert.Equal("Summary", Assert.Single(new WorkflowService(Path.Combine(Destination, "workflows.json")).Workflows).Name);
        var record = Assert.Single(new HistoryService(Path.Combine(Destination, "history.json")).Records);
        Assert.Equal("final", record.FinalText);
        Assert.Null(record.AudioFileName);
        Assert.False(File.Exists(Path.Combine(Destination, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(Destination, "Plugins")));
        Assert.True(File.Exists(Path.Combine(Destination, LegacyDailyProfileMigration.ReceiptName)));
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("staged")]
    public async Task InterruptedAttemptPublishesNothingAndCanRetry(string stop)
    {
        Seed();
        await Assert.ThrowsAsync<IOException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination,
            point => { if (point == stop) throw new IOException("simulated interruption"); }));
        Assert.False(Directory.Exists(Destination));
        Assert.Empty(Directory.GetDirectories(_root, ".typewhisper-import-*"));
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
    }

    [Fact]
    public async Task ExistingProfileIsNeverMergedEvenWhenEmpty()
    {
        Seed();
        Directory.CreateDirectory(Destination);
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task InvalidSourceDoesNotBecomeAnEmptySuccessfulImport()
    {
        var data = Seed();
        File.WriteAllText(Path.Combine(data, "history.json"), "not json");
        await Assert.ThrowsAnyAsync<Exception>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(Destination));
        Assert.Equal("not json", File.ReadAllText(Path.Combine(data, "history.json")));
    }

    [Fact]
    public async Task ConcurrentDestinationCreationIsPreserved()
    {
        Seed();
        await Assert.ThrowsAsync<IOException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination, point =>
        {
            if (point != "staged") return;
            Directory.CreateDirectory(Destination);
            File.WriteAllText(Path.Combine(Destination, "new-data"), "keep");
        }));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Destination, "new-data")));
        Assert.Single(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task AbsentSourceCreatesNothing()
    {
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task CancellationBeforePublicationAllowsRetry()
    {
        Seed();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination,
            point => { if (point == "staged") cancellation.Cancel(); }, cancellation.Token));
        Assert.False(Directory.Exists(Destination));
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
    }

    [Fact]
    public async Task UnknownFieldsAreRejectedInsteadOfSilentlyDiscarded()
    {
        var data = Seed();
        File.WriteAllText(Path.Combine(data, "dictionary.json"), "[{\"Id\":\"word\",\"Original\":\"word\",\"FutureFeature\":true}]");
        await Assert.ThrowsAnyAsync<Exception>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(Destination));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
