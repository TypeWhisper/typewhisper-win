using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class LexiconTransferTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-lexicon-transfer-" + Guid.NewGuid().ToString("N"));
    private string DictionaryPath => Path.Combine(_directory, "dictionary.json");
    private string SnippetPath => Path.Combine(_directory, "snippets.json");
    private static readonly DateTime SavedAt = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static DictionaryEntry Word(string id, string value) => new() { Id = id, Original = value, EntryType = DictionaryEntryType.Term, CreatedAt = SavedAt, UpdatedAt = SavedAt };
    private static Snippet Snippet(string id, string trigger) => new() { Id = id, Trigger = trigger, Replacement = "Hello\n{date}", CreatedAt = SavedAt, UpdatedAt = SavedAt };

    [Fact]
    public void WindowsExportRoundTripPreservesFieldsAndRemainsReadableByCore()
    {
        var entry = Word("word", "TypeWhisper") with { CtcMinSimilarity = .65f, IsEnabled = false, UsageCount = 7 };
        var json = LexiconTransfer.WriteDictionary([entry]);
        Assert.Equal(entry, Assert.Single(LexiconTransfer.ReadDictionary(json)));
        Assert.Equal(entry, Assert.Single(JsonSerializer.Deserialize<DictionaryEntry[]>(json)!));
        var snippet = Snippet("snippet", "signature") with { CaseSensitive = true, Tags = "mail, work", UsageCount = 11 };
        Assert.Equal(snippet, Assert.Single(LexiconTransfer.ReadSnippets(LexiconTransfer.WriteSnippets([snippet]))));
    }

    [Fact]
    public void VerifiedMacDictionaryExportPreservesEnabledProvenanceAndCtcThreshold()
    {
        const string json = """
            [{"type":"term","original":"TypeWhisper","caseSensitive":false,"isEnabled":false,"ctcMinSimilarity":0.65},
             {"type":"correction","original":"type whisper","replacement":"TypeWhisper","caseSensitive":true,"isEnabled":true,"source":"autoLearned"}]
            """;
        var entries = LexiconTransfer.ReadDictionary(json);
        Assert.Equal(2, entries.Length);
        Assert.Equal(DictionaryEntryType.Term, entries[0].EntryType);
        Assert.False(entries[0].IsEnabled);
        Assert.Equal(.65f, entries[0].CtcMinSimilarity);
        Assert.Equal(DictionaryEntrySource.AutoLearned, entries[1].Source);
        Assert.True(entries[1].CaseSensitive);
        Assert.Equal("TypeWhisper", entries[1].Replacement);
        Assert.NotEqual(entries[0].Id, entries[1].Id);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":\"a\",\"EntryType\":99,\"Original\":\"word\"}]")]
    [InlineData("[{\"Id\":\"a\",\"EntryType\":0,\"Original\":\"word\",\"FutureFlag\":true}]")]
    [InlineData("[{\"Id\":\"a\",\"EntryType\":0,\"Original\":\"word\",\"original\":\"other\"}]")]
    [InlineData("[{\"Id\":\"a\",\"EntryType\":0,\"Original\":\"word\",\"Source\":\"future\"}]")]
    [InlineData("[{\"type\":\"future\",\"original\":\"word\"}]")]
    [InlineData("[{\"type\":\"term\",\"original\":\"word\",\"ctcMinSimilarity\":0.99}]")]
    [InlineData("[{\"Id\":\"a\",\"EntryType\":1,\"Original\":\"word\"}]")]
    public void UnknownOrMalformedDictionaryInputFailsWithoutPartialImport(string json)
    {
        Directory.CreateDirectory(_directory);
        var original = LexiconTransfer.WriteDictionary([Word("existing", "Existing")]);
        File.WriteAllText(DictionaryPath, original);
        var store = new Lexicon(DictionaryPath, SnippetPath);
        Assert.NotNull(store.Import(json, snippets: false, replace: true));
        Assert.Equal(original, File.ReadAllText(DictionaryPath));
        Assert.Equal("Existing", Assert.Single(store.Entries).Key);
    }

    [Fact]
    public void AddRejectsConflictsAndReplacePreservesTermPacks()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DictionaryPath, LexiconTransfer.WriteDictionary([Word("existing", "Existing"), Word("pack:test:term", "Packed")]));
        var store = new Lexicon(DictionaryPath, SnippetPath);
        var import = LexiconTransfer.WriteDictionary([Word("new", "existing")]);
        Assert.NotNull(store.Import(import, snippets: false, replace: false));
        Assert.Equal(2, store.Entries.Count);
        Assert.Null(store.Import(import, snippets: false, replace: true));
        Assert.Equal(2, store.Entries.Count);
        Assert.Contains(store.Entries, entry => entry.FromPack && entry.Key == "Packed");
        Assert.Contains(store.Entries, entry => !entry.FromPack && entry.Key == "existing");
    }

    [Fact]
    public void SnippetImportPreservesCountersAndRejectsUnknownFieldsDuplicatesAndInvalidDates()
    {
        var entry = Snippet("one", "signature") with { UsageCount = 9 };
        Assert.Equal(9, Assert.Single(LexiconTransfer.ReadSnippets(LexiconTransfer.WriteSnippets([entry]))).UsageCount);
        Assert.Throws<JsonException>(() => LexiconTransfer.MergeSnippets([entry], [Snippet("two", "SIGNATURE")], false));
        Assert.Throws<JsonException>(() => LexiconTransfer.ReadSnippets("[{\"Id\":\"s\",\"Trigger\":\"hello\",\"Replacement\":\"world\",\"Future\":true}]"));
        Assert.Throws<JsonException>(() => LexiconTransfer.ReadSnippets(LexiconTransfer.WriteSnippets([entry with { Replacement = "{date:%}" }])));
    }

    [Fact]
    public void ImportWriteFailurePreservesInMemoryEntries()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SnippetPath, LexiconTransfer.WriteSnippets([Snippet("old", "old phrase")]));
        var store = new Lexicon(DictionaryPath, SnippetPath);
        File.Delete(SnippetPath); Directory.CreateDirectory(SnippetPath);
        Assert.NotNull(store.Import(LexiconTransfer.WriteSnippets([Snippet("new", "new phrase")]), snippets: true, replace: true));
        Assert.Equal("old phrase", Assert.Single(store.Entries).Key);
    }

    [Fact]
    public void ExistingUnknownMetadataBlocksWritesInsteadOfDiscardingIt()
    {
        Directory.CreateDirectory(_directory);
        const string original = "[{\"Id\":\"s\",\"Trigger\":\"hello\",\"Replacement\":\"world\",\"Future\":true}]";
        File.WriteAllText(SnippetPath, original);
        var store = new Lexicon(DictionaryPath, SnippetPath);
        Assert.NotNull(store.Import("[]", snippets: true, replace: true));
        Assert.Equal(original, File.ReadAllText(SnippetPath));
    }

    [Fact]
    public void ExportExcludesPacksPreservesPersonalMetadataAndRejectsLiveStorageDestination()
    {
        Directory.CreateDirectory(_directory);
        var personal = Word("personal", "Personal") with { UsageCount = 5 };
        File.WriteAllText(DictionaryPath, LexiconTransfer.WriteDictionary([personal, Word("pack:example:term", "Packed")]));
        var store = new Lexicon(DictionaryPath, SnippetPath);
        var destination = Path.Combine(_directory, "export.json");
        Assert.Null(store.Export(destination, snippets: false));
        Assert.Equal(personal, Assert.Single(LexiconTransfer.ReadDictionary(File.ReadAllText(destination))));
        Assert.NotNull(store.Export(DictionaryPath, snippets: false));
        Assert.Equal(2, JsonSerializer.Deserialize<DictionaryEntry[]>(File.ReadAllText(DictionaryPath))!.Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void FailedAtomicExportCleansTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "destination"); Directory.CreateDirectory(destination);
        var error = Record.Exception(() => LexiconTransfer.WriteFile(destination, "[]"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
