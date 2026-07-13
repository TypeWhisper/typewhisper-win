using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Tests.Services;

public sealed class TypeWhisperUserDataSyncStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dictionaryPath;
    private readonly string _snippetsPath;

    public TypeWhisperUserDataSyncStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_user_sync_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dictionaryPath = Path.Combine(_tempDir, "dictionary.json");
        _snippetsPath = Path.Combine(_tempDir, "snippets.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SnapshotExcludesManagedPackEntriesAndPreservesUserAuthoredData()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var snippets = new SnippetService(_snippetsPath);
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "manual",
            EntryType = DictionaryEntryType.Term,
            Original = "ManualTerm",
            UsageCount = 7
        });
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "pack:test:ManagedTerm",
            EntryType = DictionaryEntryType.Term,
            Original = "ManagedTerm"
        });
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "correction",
            EntryType = DictionaryEntryType.Correction,
            Original = "filler",
            Replacement = "",
            UsageCount = 5
        });
        snippets.AddSnippet(new Snippet
        {
            Id = "snippet",
            Trigger = ";sig",
            Replacement = "{date:yyyy}",
            UsageCount = 4,
            Tags = "mail,signature"
        });

        var store = new TypeWhisperUserDataSyncStore(dictionary, snippets);
        var snapshot = store.Snapshot();

        Assert.Contains(snapshot.DictionaryEntries, entry => entry.Original == "ManualTerm");
        Assert.DoesNotContain(snapshot.DictionaryEntries, entry => entry.Original == "ManagedTerm");
        Assert.Equal("", snapshot.DictionaryEntries.Single(entry => entry.Original == "filler").Replacement);
        Assert.Equal("{date:yyyy}", Assert.Single(snapshot.Snippets).Replacement);
        Assert.Equal(["mail", "signature"], Assert.Single(snapshot.Snippets).Tags);
    }

    [Fact]
    public void SnapshotAndApplyPreserveAutoLearnedSource()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var snippets = new SnippetService(_snippetsPath);
        dictionary.LearnCorrections([new CorrectionSuggestion("teh", "the")]);
        var store = new TypeWhisperUserDataSyncStore(dictionary, snippets);

        var synced = Assert.Single(store.Snapshot().DictionaryEntries);

        Assert.Equal(DictionaryEntrySource.AutoLearned, synced.Source);

        var targetPath = Path.Combine(_tempDir, "target-dictionary.json");
        var target = new DictionaryService(targetPath);
        new TypeWhisperUserDataSyncStore(target, new SnippetService(Path.Combine(_tempDir, "target-snippets.json")))
            .Apply([new UserDataSyncMutation.UpsertDictionary(synced)]);

        Assert.Equal(DictionaryEntrySource.AutoLearned, Assert.Single(target.Entries).Source);
    }

    [Fact]
    public void LegacySyncEntryDefaultsToManual()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var store = new TypeWhisperUserDataSyncStore(dictionary, new SnippetService(_snippetsPath));

        store.Apply([new UserDataSyncMutation.UpsertDictionary(new UserDataSyncDictionaryEntry(
            UserDataSyncDictionaryEntryType.Correction,
            "teh",
            "the",
            false,
            true,
            Date(1),
            Date(2))) ]);

        Assert.Equal(DictionaryEntrySource.Manual, Assert.Single(dictionary.Entries).Source);
    }

    [Fact]
    public void ApplyMergesDuplicateNaturalKeysAndPreservesRemoteTimestamps()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var snippets = new SnippetService(_snippetsPath);
        var existingDictionaryCreatedAt = Date(1);
        var existingSnippetCreatedAt = Date(2);
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "local-term",
            EntryType = DictionaryEntryType.Term,
            Original = "TypeWhisper",
            UsageCount = 3,
            CreatedAt = existingDictionaryCreatedAt,
            UpdatedAt = Date(10)
        });
        snippets.AddSnippet(new Snippet
        {
            Id = "local-snippet",
            Trigger = ";sig",
            Replacement = "Old",
            UsageCount = 2,
            CreatedAt = existingSnippetCreatedAt,
            UpdatedAt = Date(10)
        });

        var store = new TypeWhisperUserDataSyncStore(dictionary, snippets);
        store.Apply([
            new UserDataSyncMutation.UpsertDictionary(new UserDataSyncDictionaryEntry(
                UserDataSyncDictionaryEntryType.Term,
                " typewhisper ",
                null,
                false,
                true,
                Date(30),
                Date(40))),
            new UserDataSyncMutation.UpsertSnippet(new UserDataSyncSnippet(
                ";SIG",
                "New",
                false,
                true,
                [],
                Date(31),
                Date(41)))
        ]);

        var dictionaryMatch = Assert.Single(dictionary.Entries, entry =>
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) ==
            UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Term, "typewhisper"));
        var snippetMatch = Assert.Single(snippets.Snippets, snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) ==
            UserDataSyncIdentity.SnippetItemId(";sig"));

        Assert.Equal(" typewhisper ", dictionaryMatch.Original);
        Assert.Equal(3, dictionaryMatch.UsageCount);
        Assert.Equal(existingDictionaryCreatedAt, dictionaryMatch.CreatedAt);
        Assert.Equal(Date(40), dictionaryMatch.UpdatedAt);
        Assert.Equal("New", snippetMatch.Replacement);
        Assert.Equal(2, snippetMatch.UsageCount);
        Assert.Equal(existingSnippetCreatedAt, snippetMatch.CreatedAt);
        Assert.Equal(Date(41), snippetMatch.UpdatedAt);
    }

    [Fact]
    public void ApplyDeletesByNaturalKey()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var snippets = new SnippetService(_snippetsPath);
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "local-correction",
            EntryType = DictionaryEntryType.Correction,
            Original = "teh",
            Replacement = "the"
        });
        snippets.AddSnippet(new Snippet
        {
            Id = "local-snippet",
            Trigger = ";sig",
            Replacement = "Signature"
        });

        var store = new TypeWhisperUserDataSyncStore(dictionary, snippets);
        store.Apply([
            new UserDataSyncMutation.DeleteDictionary(
                UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Correction, "TEH")),
            new UserDataSyncMutation.DeleteSnippet(
                UserDataSyncIdentity.SnippetItemId(";SIG"))
        ]);

        Assert.Empty(dictionary.Entries);
        Assert.Empty(snippets.Snippets);
    }

    [Fact]
    public void RemoteDictionaryMutationsDoNotTouchPackBackedTerms()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var snippets = new SnippetService(_snippetsPath);
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "pack:test:React",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        var store = new TypeWhisperUserDataSyncStore(dictionary, snippets);
        store.Apply([
            new UserDataSyncMutation.UpsertDictionary(new UserDataSyncDictionaryEntry(
                UserDataSyncDictionaryEntryType.Term,
                "react",
                null,
                true,
                false,
                Date(3),
                Date(4)))
        ]);

        Assert.Equal(2, dictionary.Entries.Count);
        Assert.Contains(dictionary.Entries, entry => entry.Id == "pack:test:React" && entry.Original == "React");
        var synced = Assert.Single(dictionary.Entries, entry => entry.Id != "pack:test:React");
        Assert.Equal("react", synced.Original);
        Assert.True(synced.CaseSensitive);
        Assert.False(synced.IsEnabled);

        store.Apply([
            new UserDataSyncMutation.DeleteDictionary(
                UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Term, "REACT"))
        ]);

        var remaining = Assert.Single(dictionary.Entries);
        Assert.Equal("pack:test:React", remaining.Id);
    }

    [Fact]
    public void RemoteApplyDoesNotNotifyLocalChangeObservers()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var snippets = new SnippetService(_snippetsPath);
        var store = new TypeWhisperUserDataSyncStore(dictionary, snippets);
        var calls = 0;
        store.ObserveLocalChanges(() => calls++);

        store.Apply([
            new UserDataSyncMutation.UpsertDictionary(new UserDataSyncDictionaryEntry(
                UserDataSyncDictionaryEntryType.Term,
                "TypeWhisper",
                null,
                false,
                true,
                Date(1),
                Date(2)))
        ]);

        Assert.Equal(0, calls);

        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "manual",
            EntryType = DictionaryEntryType.Term,
            Original = "Manual"
        });

        Assert.Equal(1, calls);
    }

    private static DateTime Date(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + (long)(seconds * 1000)).UtcDateTime;
}
