using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Models.Backup;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class PersistedProfileBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-backup-tests-" + Guid.NewGuid().ToString("N"));
    private const BackupCategory Selected = BackupCategory.Dictionary | BackupCategory.Snippets;
    private string Profile(string name)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Seed(string root, string value)
    {
        new DictionaryService(Path.Combine(root, "dictionary.json")).AddEntry(new DictionaryEntry
        { Id = value, EntryType = DictionaryEntryType.Term, Original = value, CtcMinSimilarity = 0.7f });
        new SnippetService(Path.Combine(root, "snippets.json")).AddSnippet(new Snippet
        { Id = value, Trigger = value, Replacement = value + " expanded" });
    }

    private async Task<string> Archive()
    {
        var source = Profile("source");
        Seed(source, "incoming");
        return await new PersistedProfileBackup(source).ExportAsync(Selected);
    }

    [Fact]
    public async Task WorkflowAndHistoryRoundTripKeepMetadataButNeverRestoreAudioPaths()
    {
        var source = Profile("source");
        new WorkflowService(Path.Combine(source, "workflows.json")).AddWorkflow(new Workflow
        { Id = "manual", Name = "Custom", Template = WorkflowTemplate.Custom, Trigger = WorkflowTrigger.Manual() });
        new HistoryService(Path.Combine(source, "history.json")).AddRecord(new TranscriptionRecord
        { Id = "record", Timestamp = DateTime.UtcNow, RawText = "raw", FinalText = "final", SourceKind = "future-kind", AudioFileName = "private.wav" });
        var categories = BackupCategory.Workflows | BackupCategory.History;
        var archive = await new PersistedProfileBackup(source).ExportAsync(categories);
        Assert.DoesNotContain("private.wav", archive);
        var root = Profile("target"); var helper = new PersistedProfileBackup(root);
        var preview = await helper.PreviewAsync(archive, categories);
        Assert.Equal(2, preview.ChangedFileCount);
        Assert.True(helper.Apply(preview).Applied);
        Assert.Equal(WorkflowTriggerKind.Manual, Assert.Single(new WorkflowService(Path.Combine(root, "workflows.json")).Workflows).Trigger.Kind);
        var history = new HistoryService(Path.Combine(root, "history.json"));
        await history.EnsureLoadedAsync();
        var record = Assert.Single(history.Records);
        Assert.Equal("future-kind", record.SourceKind); Assert.Null(record.AudioFileName);
    }

    [Fact]
    public async Task PreviewDoesNotMutateAndApplyMergesOnlySelectedNamedFiles()
    {
        var json = await Archive();
        var root = Profile("target"); Seed(root, "existing");
        File.WriteAllText(Path.Combine(root, "history.json"), "[]");
        File.WriteAllText(Path.Combine(root, "settings.json"), "private settings must not be read");
        File.WriteAllText(Path.Combine(root, "credentials.json"), "private credentials must not be read");
        var before = File.ReadAllBytes(Path.Combine(root, "dictionary.json"));
        var helper = new PersistedProfileBackup(root);
        var preview = await helper.PreviewAsync(json, BackupCategory.Dictionary);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        Assert.Equal(1, preview.ChangedFileCount);
        Assert.Equal(1, preview.Merge.Categories[BackupCategory.Dictionary].Imported);
        var result = helper.Apply(preview);
        Assert.True(result.Applied, result.Error); Assert.False(result.RecoveryRequired);
        Assert.Equal(2, new DictionaryService(Path.Combine(root, "dictionary.json")).Entries.Count);
        Assert.Equal("existing", Assert.Single(new SnippetService(Path.Combine(root, "snippets.json")).Snippets).Trigger);
        Assert.Equal("[]", File.ReadAllText(Path.Combine(root, "history.json")));
        Assert.Equal("private credentials must not be read", File.ReadAllText(Path.Combine(root, "credentials.json")));
        Assert.Equal("private settings must not be read", File.ReadAllText(Path.Combine(root, "settings.json")));
        Assert.False(helper.HasPendingRecovery);
        var restarted = new PersistedProfileBackup(root);
        var repeated = await restarted.PreviewAsync(json, BackupCategory.Dictionary);
        Assert.Equal(0, repeated.ChangedFileCount);
    }

    [Fact]
    public async Task EmptyArchiveDoesNotCreateMissingProfileFiles()
    {
        var source = new PersistedProfileBackup(Profile("empty"));
        var targetRoot = Profile("target"); var target = new PersistedProfileBackup(targetRoot);
        var preview = await target.PreviewAsync(await source.ExportAsync(PersistedProfileBackup.SupportedCategories), PersistedProfileBackup.SupportedCategories);
        Assert.Equal(0, preview.ChangedFileCount);
        Assert.Null(target.Apply(preview).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetRoot));
    }

    [Fact]
    public async Task ChangeToUnselectedCategoryInvalidatesPreviewWithoutLosingNewData()
    {
        var root = Profile("target"); Seed(root, "existing");
        var helper = new PersistedProfileBackup(root);
        var preview = await helper.PreviewAsync(await Archive(), BackupCategory.Dictionary);
        new SnippetService(Path.Combine(root, "snippets.json")).AddSnippet(new Snippet { Id = "concurrent", Trigger = "concurrent", Replacement = "new" });
        var result = helper.Apply(preview);
        Assert.False(result.Applied); Assert.Contains("changed after preview", result.Error);
        Assert.Single(new DictionaryService(Path.Combine(root, "dictionary.json")).Entries);
        Assert.Equal(2, new SnippetService(Path.Combine(root, "snippets.json")).Snippets.Count);
        Assert.False(helper.HasPendingRecovery);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("missing-format")]
    public async Task InvalidArchiveIsRejectedWithoutProfileMutation(string defect)
    {
        var node = JsonNode.Parse(await Archive())!.AsObject();
        switch (defect)
        {
            case "unknown": node["destinationPath"] = "../outside.json"; break;
            case "duplicate": node["FORMAT"] = SettingsBackupDocument.CurrentFormat; break;
            case "version": node["schemaVersion"] = 999; break;
            case "missing-format": node.Remove("format"); break;
        }
        var root = Profile("target"); Seed(root, "existing");
        var before = File.ReadAllBytes(Path.Combine(root, "dictionary.json"));
        var helper = new PersistedProfileBackup(root);
        var error = await Record.ExceptionAsync(() => helper.PreviewAsync(node.ToJsonString(), Selected));
        Assert.True(error is InvalidDataException or JsonException, error?.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        Assert.False(helper.HasPendingRecovery);
    }

    [Fact]
    public async Task FailedSecondPublicationRestoresExactOriginalBytes()
    {
        var root = Profile("target"); Seed(root, "existing");
        var originalDictionary = File.ReadAllBytes(Path.Combine(root, "dictionary.json"));
        var originalSnippets = File.ReadAllBytes(Path.Combine(root, "snippets.json"));
        var helper = new PersistedProfileBackup(root, checkpoint =>
        { if (checkpoint == "BeforePublish:snippets.json") throw new IOException("Injected publish failure"); });
        var result = helper.Apply(await helper.PreviewAsync(await Archive(), Selected));
        Assert.False(result.Applied); Assert.False(result.RecoveryRequired); Assert.NotNull(result.Error);
        Assert.Equal(originalDictionary, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        Assert.Equal(originalSnippets, File.ReadAllBytes(Path.Combine(root, "snippets.json")));
        Assert.True(new PersistedProfileBackup(root).RecoverPending().CanOpenProfile);
    }

    [Fact]
    public async Task FailedRollbackBlocksWritersUntilRestartRecoveryCompletes()
    {
        var root = Profile("target"); Seed(root, "existing");
        var original = File.ReadAllBytes(Path.Combine(root, "dictionary.json"));
        var helper = Interrupted(root);
        var preview = await helper.PreviewAsync(await Archive(), Selected);
        var result = helper.Apply(preview);
        Assert.False(result.Applied); Assert.True(result.RecoveryRequired);
        Assert.NotEqual(original, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => helper.ExportAsync(Selected));
        // Rejecting another Apply must not delete a previous transaction's recovery copies.
        Assert.True(helper.Apply(preview).RecoveryRequired);
        var recovered = new PersistedProfileBackup(root).RecoverPending();
        Assert.True(recovered.CanOpenProfile, recovered.Error);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        Assert.False(helper.HasPendingRecovery);
    }

    [Fact]
    public async Task RollbackOfNewProfileRemovesOnlyTransactionCreatedFiles()
    {
        var root = Profile("target"); File.WriteAllText(Path.Combine(root, "unrelated.txt"), "retain");
        var helper = Interrupted(root);
        Assert.True(helper.Apply(await helper.PreviewAsync(await Archive(), Selected)).RecoveryRequired);
        Assert.True(new PersistedProfileBackup(root).RecoverPending().CanOpenProfile);
        Assert.False(File.Exists(Path.Combine(root, "dictionary.json")));
        Assert.False(File.Exists(Path.Combine(root, "snippets.json")));
        Assert.Equal("retain", File.ReadAllText(Path.Combine(root, "unrelated.txt")));
    }

    [Fact]
    public async Task CommittedCleanupFailureKeepsNewDataAndRecoveryOnlyCleansArtifacts()
    {
        var root = Profile("target"); Seed(root, "existing");
        var helper = new PersistedProfileBackup(root, checkpoint =>
        { if (checkpoint == "Committed") throw new IOException("Injected cleanup interruption"); });
        var result = helper.Apply(await helper.PreviewAsync(await Archive(), Selected));
        Assert.True(result.Applied); Assert.True(result.RecoveryRequired);
        var committed = File.ReadAllBytes(Path.Combine(root, "dictionary.json"));
        var restarted = new PersistedProfileBackup(root);
        Assert.True(restarted.RecoverPending().CanOpenProfile);
        Assert.Equal(committed, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        Assert.Equal(2, new DictionaryService(Path.Combine(root, "dictionary.json")).Entries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptJournalOrExternallyChangedTargetBlocksRecoveryAndPreservesEvidence(bool changeTarget)
    {
        var root = Profile("target"); Seed(root, "existing");
        var helper = Interrupted(root);
        Assert.True(helper.Apply(await helper.PreviewAsync(await Archive(), Selected)).RecoveryRequired);
        if (changeTarget) File.WriteAllText(Path.Combine(root, "dictionary.json"), "external edit");
        else File.WriteAllText(Path.Combine(root, ".profile-restore", "journal.json"), "{invalid}");
        var before = File.ReadAllBytes(Path.Combine(root, "dictionary.json"));
        var result = new PersistedProfileBackup(root).RecoverPending();
        Assert.False(result.CanOpenProfile); Assert.NotNull(result.Error);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "dictionary.json")));
        Assert.True(File.Exists(Path.Combine(root, ".profile-restore", "original-dictionary.json")));
    }

    [Fact]
    public async Task JournalCannotRedirectRollbackToBackupSuppliedPath()
    {
        var root = Profile("target"); Seed(root, "existing");
        var helper = Interrupted(root);
        Assert.True(helper.Apply(await helper.PreviewAsync(await Archive(), Selected)).RecoveryRequired);
        var journalPath = Path.Combine(root, ".profile-restore", "journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        journal["files"]![0]!["name"] = "../outside.json";
        File.WriteAllText(journalPath, journal.ToJsonString());
        var outside = Path.Combine(_directory, "outside.json"); File.WriteAllText(outside, "retain");
        Assert.False(new PersistedProfileBackup(root).RecoverPending().CanOpenProfile);
        Assert.Equal("retain", File.ReadAllText(outside));
    }

    [Fact]
    public async Task PreviewIsBoundToTheHelperThatCapturedItsProfile()
    {
        var root = Profile("target"); var helper = new PersistedProfileBackup(root);
        var preview = await helper.PreviewAsync(await Archive(), Selected);
        var result = new PersistedProfileBackup(root).Apply(preview);
        Assert.False(result.Applied); Assert.NotNull(result.Error); Assert.False(result.RecoveryRequired);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    private static PersistedProfileBackup Interrupted(string root) => new(root, checkpoint =>
    {
        if (checkpoint is "BeforePublish:snippets.json" or "BeforeRollback:dictionary.json")
            throw new IOException("Injected transaction interruption");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyCleanupDirectoryOrInterruptedFirstJournalWriteCanRecover(bool temporaryJournal)
    {
        var root = Profile("target");
        var transaction = Path.Combine(root, ".profile-restore"); Directory.CreateDirectory(transaction);
        if (temporaryJournal) File.WriteAllText(Path.Combine(transaction, "journal.json." + Guid.NewGuid().ToString("N") + ".tmp"), "partial write");
        var helper = new PersistedProfileBackup(root);
        Assert.True(helper.RecoverPending().CanOpenProfile);
        Assert.False(helper.HasPendingRecovery);
    }

    [Theory]
    [InlineData("journal.json.tmp")]
    [InlineData("original-dictionary.json")]
    [InlineData("unrelated.txt")]
    public void MissingJournalWithUnknownOrOriginalArtifactsStaysBlocked(string name)
    {
        var root = Profile("target");
        var transaction = Path.Combine(root, ".profile-restore"); Directory.CreateDirectory(transaction);
        var artifact = Path.Combine(transaction, name); File.WriteAllText(artifact, "retain");
        Assert.False(new PersistedProfileBackup(root).RecoverPending().CanOpenProfile);
        Assert.Equal("retain", File.ReadAllText(artifact));
    }

    [Fact]
    public async Task CompletedJournalCleansOnlyRecognizedInterruptedTemporaryWrites()
    {
        var root = Profile("target");
        var helper = new PersistedProfileBackup(root, phase => { if (phase == "Committed") throw new IOException("interrupt"); });
        Assert.True(helper.Apply(await helper.PreviewAsync(await Archive(), Selected)).RecoveryRequired);
        File.WriteAllText(Path.Combine(root, ".profile-restore", "original-dictionary.json." + Guid.NewGuid().ToString("N") + ".tmp"), "partial");
        var recovered = new PersistedProfileBackup(root).RecoverPending();
        Assert.True(recovered.CanOpenProfile, recovered.Error);
        Assert.False(helper.HasPendingRecovery);
        Assert.Equal("incoming", Assert.Single(new DictionaryService(Path.Combine(root, "dictionary.json")).Entries).Original);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
