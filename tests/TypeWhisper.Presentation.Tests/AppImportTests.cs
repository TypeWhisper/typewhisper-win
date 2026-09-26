using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public sealed class AppImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-app-import-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_directory, "flow.sqlite");
    private string DictionaryPath => Path.Combine(_directory, "dictionary.json");
    private string SnippetPath => Path.Combine(_directory, "snippets.json");
    public AppImportTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void WisprWordsPreserveCanonicalSpellingAndCorrectionAndIgnoreDeletedAndSnippetRows()
    {
        var batch = LexiconAppImport.MapWispr([
            new("type whisper", "TypeWhisper", false, false), new("übermorgen", null, false, false),
            new("deleted", null, true, false), new("signature", "Hello", false, true)], false);
        var review = LexiconAppImport.Review(batch, false, null);
        Assert.Equal(3, review.Additions);
        Assert.Equal(2, review.Excluded);
        var entries = LexiconTransfer.ReadDictionary(review.Json);
        Assert.Contains(entries, e => e.EntryType == DictionaryEntryType.Term && e.Original == "TypeWhisper");
        Assert.Contains(entries, e => e.EntryType == DictionaryEntryType.Correction && e.Original == "type whisper" && e.Replacement == "TypeWhisper");
        Assert.Contains(entries, e => e.Original == "übermorgen");
    }

    [Fact]
    public void DuplicateAndConflictingSnippetsNeverReplaceExistingContentOrMetadata()
    {
        var existing = new Snippet { Id = "existing", Trigger = "signature", Replacement = "My signature", IsEnabled = false, UsageCount = 12, Tags = "work" };
        var baseline = LexiconTransfer.WriteSnippets([existing]);
        var batch = LexiconAppImport.MapWispr([
            new("SIGNATURE", "Another signature", false, true), new("new phrase", "line 1\nline 2", false, true),
            new("new phrase", "line 1\nline 2", false, true), new("new phrase", "different", false, true)], true);
        var review = LexiconAppImport.Review(batch, true, baseline);
        Assert.Equal(1, review.Additions);
        Assert.Equal(2, review.Lines.Count(e => e.Outcome == AppImportOutcome.Conflict));
        Assert.Single(review.Lines, e => e.Outcome == AppImportOutcome.Duplicate);
        Assert.Equal(existing, LexiconTransfer.ReadSnippets(review.Json)[0]);
        Assert.Equal("line 1\nline 2", LexiconTransfer.ReadSnippets(review.Json)[1].Replacement);
    }

    [Fact]
    public void ReimportIsIdempotentAndPreservesPacksAndDisabledEntries()
    {
        var existing = new DictionaryEntry { Id = "pack:test:word", EntryType = DictionaryEntryType.Term, Original = "TypeWhisper" };
        var disabled = new DictionaryEntry { Id = "disabled", EntryType = DictionaryEntryType.Term, Original = "Disabled", IsEnabled = false };
        var batch = LexiconAppImport.MapWispr([new("TypeWhisper", null, false, false), new("new", null, false, false), new("disabled", null, false, false)], false);
        var review = LexiconAppImport.Review(batch, false, LexiconTransfer.WriteDictionary([existing, disabled]));
        Assert.Equal(1, review.Additions);
        Assert.Contains(review.Lines, e => e.Key == "disabled" && e.Outcome == AppImportOutcome.Conflict);
        Assert.Equal(existing, LexiconTransfer.ReadDictionary(review.Json, true)[0]);
        Assert.Equal(disabled, LexiconTransfer.ReadDictionary(review.Json, true)[1]);
        Assert.Equal(0, LexiconAppImport.Review(batch, false, review.Json).Additions);
    }

    [Theory]
    [InlineData("{clipboard}")]
    [InlineData("Today's date: {date:yyyy-MM-dd}")]
    [InlineData("{day} {year}")]
    [InlineData("{time}")]
    [InlineData("")]
    public void ForeignSnippetsCannotAcquirePlaceholderSemantics(string text)
    {
        var review = LexiconAppImport.Review(LexiconAppImport.MapWispr([new("trigger", text, false, true)], true), true, null);
        Assert.Equal(0, review.Additions);
        Assert.Equal(AppImportOutcome.Unsupported, Assert.Single(review.Lines).Outcome);
    }

    [Fact]
    public void LiteralCorrectionEscapesCannotBecomeFormattingCommands()
    {
        var batch = LexiconAppImport.MapWispr([new("path", @"C:\new", false, false)], false);
        var review = LexiconAppImport.Review(batch, false, null);
        Assert.Contains(review.Lines, line => line.Kind == "Correction" && line.Outcome == AppImportOutcome.Unsupported);
        Assert.DoesNotContain(LexiconTransfer.ReadDictionary(review.Json), entry => entry.EntryType == DictionaryEntryType.Correction);
    }

    [Fact]
    public void InvalidWordsAreShownAsExcludedWithoutDiscardingValidWords()
    {
        var review = LexiconAppImport.Review(LexiconAppImport.MapWispr([
            new("", null, false, false), new("a\nb", null, false, false), new(new string('a', 161), null, false, false), new("valid", null, false, false)], false), false, null);
        Assert.Equal(1, review.Additions);
        Assert.Equal(3, review.Lines.Count(e => e.Outcome == AppImportOutcome.Unsupported));
    }

    [Fact]
    public void HandyReadsOnlyCustomWordsAndRetriesTornReads()
    {
        var json = Encoding.UTF8.GetBytes("""{"settings":{"custom_words":[" München ","TypeWhisper"],"custom_filler_words":["remove me"]},"unrelated":"ignored"}""");
        var reads = new Queue<byte[]>([[], json, json, json]);
        var batch = LexiconAppImport.ReadHandy("unused", _ => reads.Dequeue());
        Assert.Equal(new[] { "München", "TypeWhisper" }, batch.Dictionary.Select(e => e.Original));
        Assert.Empty(reads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"settings\":null}")]
    [InlineData("{\"settings\":{\"custom_words\":null}}")]
    public void HandyOptionalSettingsMatchEmptyWordList(string json) => Assert.Empty(LexiconAppImport.ReadHandy("unused", _ => Encoding.UTF8.GetBytes(json)).Dictionary);

    [Theory]
    [InlineData("{\"settings\":{\"custom_words\":[1]}}")]
    [InlineData("{\"settings\":{\"custom_words\":[],\"custom_words\":[\"unexpected\"]}}")]
    [InlineData("{\"settings\":[]}")]
    [InlineData("")]
    public void MalformedHandySettingsAreRejected(string json) => Assert.ThrowsAny<JsonException>(() => LexiconAppImport.ReadHandy("unused", _ => Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ContinuouslyChangingHandySourceIsRejected()
    {
        var index = 0;
        Assert.Throws<IOException>(() => LexiconAppImport.ReadHandy("unused", _ => Encoding.UTF8.GetBytes((index++).ToString())));
        Assert.Equal(6, index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewDoesNotWriteAndStaleReviewCannotOverwriteCurrentCatalog(bool snippets)
    {
        var store = new Lexicon(DictionaryPath, SnippetPath);
        var batch = LexiconAppImport.MapWispr([new("new", snippets ? "text" : null, false, snippets)], snippets);
        var review = store.ReviewAppImport(batch, snippets);
        var path = snippets ? SnippetPath : DictionaryPath;
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "[]");
        Assert.Contains("changed while", store.CommitAppImport(review));
        Assert.Equal("[]", File.ReadAllText(path));
        review = store.ReviewAppImport(batch, snippets);
        Assert.Null(store.CommitAppImport(review));
        Assert.Single(store.Entries);
        Assert.Equal(0, store.ReviewAppImport(batch, snippets).Additions);
    }

    [Fact]
    public void FailedSaveKeepsTheReviewAndInMemoryCatalogUnchanged()
    {
        var store = new Lexicon(DictionaryPath, SnippetPath);
        var review = store.ReviewAppImport(LexiconAppImport.MapWispr([new("new", null, false, false)], false), false);
        Directory.CreateDirectory(DictionaryPath);
        Assert.NotNull(store.CommitAppImport(review));
        Assert.Empty(store.Entries);
        Assert.Empty(Directory.GetFileSystemEntries(DictionaryPath));
    }

    [Fact]
    public void CancelingAcquisitionStopsBeforeParsingAndCleansTheCopy()
    {
        File.WriteAllText(Source, "source");
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        Assert.Throws<OperationCanceledException>(() => StableImportCopy.Read(Source, _ => ++reads,
            afterCopy: cancellation.Cancel, scratchParent: _directory, cancellationToken: cancellation.Token));
        Assert.Equal(0, reads);
        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [Fact]
    public void CancellationStopsHandyBeforeItsSecondRead()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        Assert.Throws<OperationCanceledException>(() => LexiconAppImport.ReadHandy("unused", _ =>
        { reads++; cancellation.Cancel(); return Encoding.UTF8.GetBytes("{}"); }, cancellation.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void DeterministicReaderFailureDoesNotRecopyTheDatabase()
    {
        File.WriteAllText(Source, "stable");
        var copies = 0;
        Assert.Throws<IOException>(() => StableImportCopy.Read<int>(Source, _ => throw new IOException("Unsupported schema"),
            afterCopy: () => copies++, scratchParent: _directory));
        Assert.Equal(1, copies);
        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [Fact]
    public void CleanupRemovesOnlyAbandonedOwnedCopiesAndPreservesActiveAndRecentImports()
    {
        string Create(string name, bool old)
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, ".lease"), "");
            File.WriteAllText(Path.Combine(path, "flow.sqlite"), "private copied data");
            if (old) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            return path;
        }
        var abandoned = Create("typewhisper-import-" + Guid.NewGuid().ToString("N"), true);
        var active = Create("typewhisper-import-" + Guid.NewGuid().ToString("N"), true);
        var recent = Create("typewhisper-import-" + Guid.NewGuid().ToString("N"), false);
        var unrelated = Create("typewhisper-import-user-notes", true);
        using (new FileStream(Path.Combine(active, ".lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            StableImportCopy.CleanupAbandonedCopies(_directory);
            Assert.False(Directory.Exists(abandoned));
            Assert.True(Directory.Exists(active));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(unrelated));
        }
        StableImportCopy.CleanupAbandonedCopies(_directory);
        Assert.False(Directory.Exists(active));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeCatalogIsRejectedBeforeEnumeratingAllCandidatesOrSerializingTheWholeArray(bool snippets)
    {
        var replacement = new string('x', 10000);
        var words = new CountingCandidates<DictionaryEntry>(i => new()
        { Id = i.ToString(), Original = "phrase " + i, EntryType = DictionaryEntryType.Correction, Replacement = replacement });
        var expansions = new CountingCandidates<Snippet>(i => new()
        { Id = i.ToString(), Trigger = "phrase " + i, Replacement = replacement });
        var batch = new AppImportBatch(snippets ? [] : words, snippets ? expansions : [], 0);
        Assert.Throws<InvalidDataException>(() => LexiconAppImport.Review(batch, snippets, null));
        Assert.InRange(snippets ? expansions.ReadCount : words.ReadCount, 1, 500);
    }

    private sealed class CountingCandidates<T>(Func<int, T> create) : IReadOnlyList<T>
    {
        public int ReadCount { get; private set; }
        public int Count => 10000;
        public T this[int index] => create(index);
        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) { ReadCount++; yield return create(i); }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void StableCopyRejectsSameSizeAndTimestampMutationAndRemovesScratchFiles()
    {
        File.WriteAllText(Source, "initial");
        var stamp = File.GetLastWriteTimeUtc(Source);
        var count = 0;
        var readCalled = false;
        Assert.Throws<IOException>(() => StableImportCopy.Read(Source, _ => { readCalled = true; return 0; }, () =>
        {
            File.WriteAllText(Source, count++ % 2 == 0 ? "changed" : "initial");
            File.SetLastWriteTimeUtc(Source, stamp);
        }, _directory));
        Assert.False(readCalled);
        Assert.Equal(3, count);
        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [Fact]
    public void RollbackJournalPreventsCopyAndRead()
    {
        File.WriteAllText(Source, "source");
        File.WriteAllText(Source + "-journal", "pending");
        Assert.Throws<IOException>(() => StableImportCopy.Read(Source, _ => 0, scratchParent: _directory));
        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [WindowsSqliteFact]
    public void RealSqliteWalRowsAreReadWithoutChangingAnySourcePart()
    {
        var db = CreateDatabase(wal: true);
        try
        {
            Execute(db, "INSERT INTO Dictionary VALUES(1,'übermorgen',NULL,0,0); INSERT INTO Dictionary VALUES(2,'signature','line 1'||char(10)||'line 2',0,1);");
            var before = SourceHashes();
            var rows = WisprImportDatabase.Read(Source);
            Assert.Equal(2, rows.Count);
            Assert.Equal("übermorgen", rows[0].Phrase);
            Assert.Equal("line 1\nline 2", rows[1].Replacement);
            Assert.Equal(before, SourceHashes());
        }
        finally { sqlite3_close_v2(db); }
    }

    [WindowsSqliteFact]
    public void RealSqliteWrongBooleanTypeFailsWithoutPartialImport()
    {
        var db = CreateDatabase();
        Execute(db, "INSERT INTO Dictionary VALUES(1,'valid',NULL,0,0); INSERT INTO Dictionary VALUES(2,'invalid',NULL,'false',0);");
        sqlite3_close_v2(db);
        Assert.Throws<IOException>(() => WisprImportDatabase.Read(Source));
    }

    [WindowsSqliteFact]
    public void SourceLimitCountsDeletedRowsBeforeFiltering()
    {
        var db = CreateDatabase();
        Execute(db, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) INSERT INTO Dictionary SELECT x,'deleted',NULL,1,0 FROM n;");
        sqlite3_close_v2(db);
        Assert.Throws<InvalidDataException>(() => WisprImportDatabase.Read(Source));
    }

    private string[] SourceHashes() => new[] { "", "-wal", "-shm", "-journal" }.Select(suffix =>
    {
        if (!File.Exists(Source + suffix)) return "missing";
        using var stream = new FileStream(Source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }).ToArray();

    private nint CreateDatabase(bool wal = false)
    {
        Assert.Equal(0, sqlite3_open_v2(Encoding.UTF8.GetBytes(Source + '\0'), out var db, 6, 0));
        Execute(db, (wal ? "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" : "") +
            "CREATE TABLE Dictionary(id INTEGER PRIMARY KEY,phrase TEXT,replacement TEXT,isDeleted,isSnippet);");
        return db;
    }
    private static void Execute(nint db, string sql) => Assert.Equal(0, sqlite3_exec(db, Encoding.UTF8.GetBytes(sql + '\0'), 0, 0, 0));
    [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_open_v2(byte[] path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_exec(nint db, byte[] sql, nint callback, nint argument, nint error);
    [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_close_v2(nint db);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

public sealed class WindowsSqliteFactAttribute : FactAttribute
{
    public WindowsSqliteFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the Windows winsqlite3 library.";
    }
}
