using System.Text;
using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryPassageChunkerTests
{
    [Fact]
    public void AShortTextIsOnePassageWithItsPlaceInTheText()
    {
        var passages = HistoryPassageChunker.Passages("  Hello world. This is short.\n");

        var passage = Assert.Single(passages);
        Assert.Equal(new HistoryPassage(HistoryPassageKind.Transcript, "Hello world. This is short.", [], TextStart: 2, TextEnd: 29), passage);
    }

    [Fact]
    public void ALongTextIsCutAtSentenceEndsIntoPassagesOfSimilarLength()
    {
        var text = string.Join(' ', Enumerable.Range(1, 33).Select(number => $"Sentence number {number:00} talks about the budget of a project."));

        var passages = HistoryPassageChunker.Passages(text, HistoryPassageKind.Result);

        Assert.Equal(3, passages.Count);
        Assert.All(passages, passage =>
        {
            Assert.Equal(HistoryPassageKind.Result, passage.Kind);
            Assert.InRange(passage.Text.Length, 400, HistoryPassageChunker.MaximumLength);
            Assert.EndsWith(".", passage.Text, StringComparison.Ordinal);
            Assert.Equal(passage.Text, text[passage.TextStart!.Value..passage.TextEnd!.Value]);
        });
        // The last passage took sentences from the one before it instead of staying short.
        Assert.True(Math.Abs(passages[1].Text.Length - passages[2].Text.Length) < 120);
    }

    [Fact]
    public void ASentenceLongerThanAPassageIsCutBetweenWords()
    {
        var text = string.Concat(Enumerable.Repeat("abcdefghi ", 150));

        var passages = HistoryPassageChunker.Passages(text);

        Assert.Equal(2, passages.Count);
        Assert.All(passages, passage =>
        {
            Assert.True(passage.Text.Length <= HistoryPassageChunker.MaximumLength);
            Assert.All(passage.Text.Split(' '), word => Assert.Equal("abcdefghi", word));
        });
    }

    [Fact]
    public void AWordLongerThanAPassageIsNeverCutInsideAnEmoji()
    {
        var text = new string('a', 899) + "😀" + new string('b', 200);

        var passages = HistoryPassageChunker.Passages(text);

        Assert.Equal(2, passages.Count);
        Assert.Equal(new string('a', 899), passages[0].Text);
        Assert.StartsWith("😀", passages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LineBreaksEndSentences()
    {
        var text = string.Join('\n', Enumerable.Range(1, 30).Select(number => $"Line {number:00} has no full stop but ends here"));

        var passages = HistoryPassageChunker.Passages(text);

        Assert.True(passages.Count >= 2);
        Assert.All(passages, passage => Assert.EndsWith("ends here", passage.Text, StringComparison.Ordinal));
    }
}

public sealed class HistoryFullTextQueryTests
{
    [Fact]
    public void WordsAreLettersAndDigitsLowercased()
    {
        Assert.Equal(["hello", "world", "grüße", "42x", "日本語"], HistoryFullTextQuery.Words("Hello, WORLD! Grüße — 42x 日本語"));
    }

    [Fact]
    public void QueryOperatorsInTheSearchTextHaveNoEffect()
    {
        Assert.Equal("\"near\"* OR \"a\" OR \"b\" OR \"x\" OR \"y\"", HistoryFullTextQuery.Expression(["NEAR(a b)", "\"x\" -y"]));
    }

    [Fact]
    public void LongWordsAlsoFindLongerWordsAndGermanSpellingsFindEachOther()
    {
        Assert.Equal("\"the\" OR \"budget\"*", HistoryFullTextQuery.Expression(["the budget"]));
        Assert.Equal("\"müller\"* OR \"mueller\"*", HistoryFullTextQuery.Expression(["Müller"]));
        Assert.Equal("straße", HistoryFullTextQuery.Variant("strasse"));
        Assert.Null(HistoryFullTextQuery.Variant("budget"));
    }

    [Fact]
    public void NamesAreSearchedAsPhrases()
    {
        Assert.Equal("\"anna schmidt\"", HistoryFullTextQuery.Expression([], ["Anna  Schmidt!"]));
    }

    [Fact]
    public void AccentsWrittenAsSeparateMarksStayInTheirWord()
    {
        Assert.Equal(["na\u00efve", "caf\u00e9"], HistoryFullTextQuery.Words("nai\u0308ve, cafe\u0301!"));
        Assert.Equal("\"na\u00efve\"*", HistoryFullTextQuery.Expression(["NAI\u0308VE"]));
    }

    [Fact]
    public void NothingToSearchForGivesNoQueryAndLongSearchesAreCapped()
    {
        Assert.Null(HistoryFullTextQuery.Expression(["...", "  "], ["-"]));
        var expression = HistoryFullTextQuery.Expression([string.Join(' ', Enumerable.Range(0, 40).Select(number => "w" + number))]);
        Assert.Equal(HistoryFullTextQuery.MaximumWords, expression!.Split(" OR ").Length);
    }
}

public sealed class HistorySearchRecordTests
{
    [Fact]
    public void ADictationKeepsItsAppAndDomainAndATextWrittenFromItIsAResult()
    {
        var record = HistorySearchTestData.Record("a", "please write an email to anna about the budget", recordedAt: 100) with
        {
            FinalText = "Dear Anna,\n\nHere is the plan for next quarter's spending.", AppName = "Mail", AppProcessName = "olk",
            AppUrl = "https://www.Example.com/inbox", Language = "en"
        };

        var indexed = HistorySearchRecord.From(record)!;

        Assert.Equal(("Mail", "olk", "example.com", (string?)null), (indexed.AppName, indexed.AppProcessName, indexed.Domain, indexed.Title));
        Assert.Equal([HistoryPassageKind.Transcript, HistoryPassageKind.Result], indexed.Passages.Select(passage => passage.Kind));
        Assert.Equal(record.RawText, indexed.Passages[0].Text);
    }

    [Fact]
    public void ACorrectedTextReplacesWhatWasSaidAndARecordWithoutAppKeepsItsNameAsTitle()
    {
        var record = HistorySearchTestData.Record("a", "the quarterly budget review is on monday morning") with
        {
            FinalText = "The quarterly budget review is on Monday morning.", AppName = "Weekly planning"
        };

        var indexed = HistorySearchRecord.From(record)!;

        Assert.Equal("Weekly planning", indexed.Title);
        Assert.Null(indexed.AppName);
        Assert.Equal(["The quarterly budget review is on Monday morning."], indexed.Passages.Select(passage => passage.Text));
    }

    [Fact]
    public void FailedOrUnfinishedRecordsAreNotIndexedAndTheHashFollowsTheContent()
    {
        var record = HistorySearchTestData.Record("a", "text");

        Assert.Null(HistorySearchRecord.From(record with { Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed }));
        Assert.Null(HistorySearchRecord.From(record with { ProcessingState = "transcribing" }));
        Assert.NotNull(HistorySearchRecord.From(record with { ProcessingState = "ready" }));
        Assert.Equal(HistorySearchRecord.HashOf(record), HistorySearchRecord.HashOf(record with { }));
        Assert.NotEqual(HistorySearchRecord.HashOf(record), HistorySearchRecord.HashOf(record with { FinalText = "other" }));
        Assert.NotEqual(HistorySearchRecord.HashOf(record), HistorySearchRecord.HashOf(record with { AppName = "" }));
    }
}

public sealed class HistorySearchIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-HistorySearchTests", Guid.NewGuid().ToString("N"), HistorySearchIndex.DirectoryName);
    private readonly HistorySearchIndex _index;

    public HistorySearchIndexTests() => _index = new(_directory);

    public void Dispose()
    {
        _index.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true); }
        catch (IOException) { }
    }

    private static HistorySearchRecord Indexed(string id, string text, long recordedAt = 1_000, string source = "dictation", string? url = null) =>
        HistorySearchRecord.From(HistorySearchTestData.Record(id, text, recordedAt, source) with { AppUrl = url, AppName = "Notes", AppProcessName = "notes" })!;

    private IReadOnlyList<string> Found(string words, HistorySearchScope? scope = null) =>
        _index.Search([words], scope: scope).Select(hit => hit.RecordId).ToArray();

    [Fact]
    public void RecordsAreFoundByTextAndTitleUntilTheyAreReplacedOrRemoved()
    {
        var titled = HistorySearchRecord.From(HistorySearchTestData.Record("t", "nothing special was said") with { AppName = "Zahnarzt Termin" })!;
        _index.Upsert([Indexed("a", "We talked about the budget."), Indexed("b", "Über den Müller wurde gesprochen."), titled]);

        Assert.Equal(["a"], Found("budgets budget"));
        Assert.Equal(["a"], Found("budg"));
        Assert.Equal(["b"], Found("uber"));
        Assert.Equal(["b"], Found("mueller"));
        Assert.Equal(["t"], Found("zahnarzt"));

        _index.Upsert([Indexed("a", "We talked about the weather.")]);
        Assert.Empty(Found("budget"));
        Assert.Equal(["a"], Found("weather"));

        _index.Remove(["a", "t"]);
        Assert.Empty(Found("weather"));
        Assert.Empty(Found("zahnarzt"));
        Assert.Equal((1, 1), _index.Counts());
    }

    [Fact]
    public void BetterMatchesRankFirstAndWordsInsideLongerWordsAreFoundWhenLittleElseIs()
    {
        _index.Upsert(
        [
            Indexed("long", "The weather report mentions the budget once among many other words about rain, wind, sunshine and the coming weekend."),
            Indexed("short", "Budget, budget, budget."),
            Indexed("compound", "Morgen ist der Termin beim Kinderzahnarzt.")
        ]);

        var hits = _index.Search(["budget"]);
        Assert.Equal(["short", "long"], hits.Select(hit => hit.RecordId));
        Assert.True(hits[0].Score >= hits[1].Score);
        Assert.Equal(["compound"], Found("zahnarzt"));
    }

    [Fact]
    public void AScopeLimitsThePeriodSourcesAppsDomainsAndRecords()
    {
        _index.Upsert(
        [
            Indexed("early", "budget plan", recordedAt: 1_000, url: "https://www.example.com/a"),
            Indexed("late", "budget plan", recordedAt: 5_000, source: "file"),
            Indexed("later", "budget plan", recordedAt: 9_000)
        ]);
        var period = new HistorySearchScope(DateTimeOffset.FromUnixTimeSeconds(4_000), DateTimeOffset.FromUnixTimeSeconds(9_000));

        Assert.Equal(["late"], Found("budget", period));
        Assert.Equal(["late"], Found("budget", new() { Sources = new HashSet<string> { "file" } }));
        Assert.Equal(["early"], Found("budget", new() { Domains = new HashSet<string> { "example.com" } }));
        Assert.Equal(3, Found("budget", new() { AppProcessNames = new HashSet<string> { "NOTES" } }).Count);
        Assert.Equal(["later"], Found("budget", new() { RecordIds = new HashSet<string> { "later" } }));
        Assert.Empty(Found("budget", new() { RecordIds = new HashSet<string>() }));
    }

    [Fact]
    public void NewestRecordsFollowTheRecordingTimeAlsoForRecordsAddedLater()
    {
        _index.Upsert([Indexed("b", "second", recordedAt: 2_000), Indexed("c", "third", recordedAt: 3_000)]);
        _index.Upsert([Indexed("a", "first", recordedAt: 1_000), Indexed("a2", "also first", recordedAt: 1_000)]);

        var (hits, total) = _index.NewestRecords(new(), 3);

        Assert.Equal(4, total);
        Assert.Equal(["c", "b"], hits.Take(2).Select(hit => hit.RecordId));
        Assert.Contains(hits[2].RecordId, new[] { "a", "a2" });
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(3_000), hits[0].RecordedAt);
        Assert.Equal(1, _index.NewestRecords(new(DateTimeOffset.FromUnixTimeSeconds(1_500), DateTimeOffset.FromUnixTimeSeconds(2_500)), 10).Total);
    }

    [Fact]
    public void ATranscriptLeavesOutResultsAndASummaryLastsUntilTheRecordChanges()
    {
        var record = HistorySearchTestData.Record("a", "please write an email to anna about the budget") with { FinalText = "Dear Anna, the budget is fine." };
        _index.Upsert([HistorySearchRecord.From(record)!]);

        var (passages, hash) = _index.Transcript("a")!.Value;
        Assert.Equal(["please write an email to anna about the budget"], passages.Select(passage => passage.Text));
        Assert.Null(_index.Transcript("missing"));

        _index.SetSummary("a", "Old summary", "provider", "stale-hash");
        Assert.Null(_index.Summary("a"));
        _index.SetSummary("a", "A summary", "provider", hash);
        _index.SetSummary("a", "Eine Zusammenfassung", "provider", hash, "de");
        Assert.Equal(("A summary", "provider"), _index.Summary("a"));
        Assert.Equal(("Eine Zusammenfassung", "provider"), _index.Summary("a", "de"));

        _index.Upsert([HistorySearchRecord.From(record with { FinalText = "Dear Anna, the budget changed." })!]);
        Assert.Null(_index.Summary("a"));
    }

    [Fact]
    public void TheVocabularyNamesAppsAndDomainsOfTheGivenRecords()
    {
        _index.Upsert([Indexed("a", "one", url: "https://example.com"), Indexed("b", "two", url: "https://docs.example.org/x")]);

        var vocabulary = _index.Vocabulary();

        Assert.Equal([new HistorySearchApp("Notes", "notes")], vocabulary.Apps);
        Assert.Equal(["docs.example.org", "example.com"], vocabulary.Domains);
        Assert.Empty(vocabulary.Speakers);
        Assert.Equal(["example.com"], _index.Vocabulary(new HashSet<string> { "a" }).Domains);
        Assert.Empty(_index.Vocabulary(new HashSet<string>()).Apps);
        Assert.Equal(2, _index.Vocabulary().Domains.Count);
    }

    [Fact]
    public void AppProcessNamesAndDomainsCompareWithoutCase()
    {
        var word = HistorySearchRecord.From(HistorySearchTestData.Record("word", "budget") with
            { AppName = "Word", AppProcessName = "WINWORD", AppUrl = "https://WWW.Example.COM/inbox" })!;
        _index.Upsert([word, Indexed("other", "budget")]);

        Assert.Equal(["word"], Found("budget", new() { AppProcessNames = new HashSet<string> { "winword" } }));
        Assert.Equal(["word"], Found("budget", new() { AppProcessNames = new HashSet<string> { " WinWord " } }));
        Assert.Equal(["word"], Found("budget", new() { Domains = new HashSet<string> { "www.example.com" } }));
        Assert.Empty(Found("budget", new() { AppProcessNames = new HashSet<string> { " " } }));
        Assert.Contains(new HistorySearchApp("Word", "winword"), _index.Vocabulary().Apps);
        Assert.Equal(["example.com"], _index.Vocabulary().Domains);
    }

    [Fact]
    public void AccentsCaseAndSeparateMarksMatchAsTyped()
    {
        _index.Upsert([Indexed("a", "Ein naïve Ansatz."), Indexed("b", "ÜBERWEISUNG an die KINDERZAHNÄRZTIN")]);

        Assert.Equal(["a"], Found("nai\u0308ve"));
        Assert.Equal(["b"], Found("uberweisung"));
        // Inside a longer word, found by the substring search, which folds case and accents like the index.
        Assert.Equal(["b"], Found("zahnarztin"));
    }

    [Fact]
    public void RecordsOfOneSecondKeepTheirOrderToTheMillisecond()
    {
        static HistorySearchRecord At(string id, long milliseconds) => HistorySearchRecord.From(HistorySearchTestData.Record(id, "budget") with
            { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime, AppProcessName = "notes" })!;
        _index.Upsert([At("late", 1_000_900)]);
        _index.Upsert([At("early", 1_000_100)]);

        Assert.Equal(["late", "early"], _index.NewestRecords(new(), 2).Hits.Select(hit => hit.RecordId));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000_900), _index.NewestRecords(new(), 1).Hits[0].RecordedAt);
        Assert.Equal(["late"], Found("budget", new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_500), DateTimeOffset.FromUnixTimeMilliseconds(1_001_000))));
        Assert.Equal(["early"], Found("budget", new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_100), DateTimeOffset.FromUnixTimeMilliseconds(1_000_900))));
        Assert.Empty(Found("budget", new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_950), DateTimeOffset.FromUnixTimeMilliseconds(1_000_950))));
    }

    [Fact]
    public void ReadingWithoutAnIndexCreatesNoFile()
    {
        Assert.Empty(Found("budget"));
        Assert.Empty(_index.ContentHashes());
        Assert.False(_index.Exists);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void ADamagedFileIsDeletedWhenReadAndBuiltAgainWhenWritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Join(_directory, HistorySearchIndex.FileName), Encoding.ASCII.GetBytes(new string('x', 8192)));
        var damaged = 0;
        _index.Damaged += () => damaged++;

        Assert.Empty(Found("budget"));
        Assert.Equal(1, damaged);
        Assert.False(_index.Exists);

        _index.Upsert([Indexed("a", "budget")]);
        Assert.Equal(["a"], Found("budget"));
    }

    [Fact]
    public void AnIndexOfAnotherFormatIsReplaced()
    {
        _index.Upsert([Indexed("a", "budget")]);
        _index.Close();
        using (var connection = new SqliteConnection($"Data Source={Path.Join(_directory, HistorySearchIndex.FileName)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE index_meta SET value = '0' WHERE key = 'format'";
            command.ExecuteNonQuery();
        }
        var damaged = 0;
        _index.Damaged += () => damaged++;

        Assert.Empty(_index.ContentHashes());
        Assert.Equal(1, damaged);
        _index.Create();
        Assert.True(_index.Exists);
        Assert.Equal((0, 0), _index.Counts());
    }

    [Fact]
    public void RemovedTextIsGoneFromTheFileAndDestroyDeletesTheFolder()
    {
        _index.Upsert([Indexed("keep", "ordinary words"), Indexed("gone", "Zyxwvutsrq is a secret word.")]);
        _index.Remove(["gone"]);
        _index.Close();

        var bytes = File.ReadAllBytes(Path.Join(_directory, HistorySearchIndex.FileName));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("yxwvutsrq"u8));

        Assert.True(_index.Destroy());
        Assert.False(Directory.Exists(_directory));
    }
}

public sealed class HistorySearchIndexerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-HistorySearchTests", Guid.NewGuid().ToString("N"), HistorySearchIndex.DirectoryName);
    private readonly HistorySearchIndex _index;
    private readonly HistorySearchTestData.FakeHistory _history = new();
    private readonly List<string> _failures = [];
    private HistorySearchIndexer? _indexer;

    public HistorySearchIndexerTests() => _index = new(_directory);

    public void Dispose()
    {
        _indexer?.StopAsync().GetAwaiter().GetResult();
        _index.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true); }
        catch (IOException) { }
    }

    private HistorySearchIndexer Indexer(TimeSpan? upsertDelay = null, Func<bool>? isBusy = null) => _indexer = new(_history, _index,
        (stage, _) => { lock (_failures) _failures.Add(stage); }, isBusy, upsertDelay ?? TimeSpan.Zero, TimeSpan.FromMilliseconds(20), TimeSpan.Zero);

    private IReadOnlyList<string> Indexed() => [.. _index.ContentHashes().Keys.Order(StringComparer.Ordinal)];

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task PreparingIndexesHistoryAndChangesFollow()
    {
        _history.Replace(HistorySearchTestData.Record("a", "budget"), HistorySearchTestData.Record("b", "weather"),
            HistorySearchTestData.Record("failed", "budget") with { Status = TranscriptionRecordStatus.TextProcessorFailed });
        var indexer = Indexer();

        await indexer.PrepareAsync();

        Assert.True(indexer.IsEnabled);
        Assert.Equal(["a", "b"], Indexed());
        _history.Replace(HistorySearchTestData.Record("a", "holiday"), _history.Records[1], HistorySearchTestData.Record("c", "new"));
        await indexer.WaitForQueuedWorkAsync();
        Assert.Equal(["a", "b", "c"], Indexed());
        Assert.Equal(["a"], _index.Search(["holiday"]).Select(hit => hit.RecordId));
        Assert.Empty(_index.Search(["budget"]));
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task RemovalsDoNotWaitForChangedRecordsOrADictation()
    {
        var busy = false;
        _history.Replace(HistorySearchTestData.Record("a", "budget"), HistorySearchTestData.Record("b", "weather"));
        var indexer = Indexer(TimeSpan.FromMinutes(5), () => busy);
        await indexer.PrepareAsync();
        busy = true;

        _history.Replace(_history.Records[1], HistorySearchTestData.Record("c", "new"));

        await Eventually(() => !Indexed().Contains("a"));
        Assert.Equal(["b"], Indexed());
    }

    [Fact]
    public async Task ClearingHistoryReplacesTheIndexFileWithAnEmptyOne()
    {
        _history.Replace(HistorySearchTestData.Record("a", "budget"));
        var indexer = Indexer();
        await indexer.PrepareAsync();

        _history.Replace();
        await indexer.WaitForQueuedWorkAsync();

        Assert.True(_index.Exists);
        Assert.Equal((0, 0), _index.Counts());
    }

    [Fact]
    public async Task DisablingDeletesTheIndexAndStartingWithoutOneDoesNothing()
    {
        _history.Replace(HistorySearchTestData.Record("a", "budget"));
        var indexer = Indexer();
        await indexer.PrepareAsync();

        await indexer.DisableAsync();

        Assert.False(indexer.IsEnabled);
        Assert.False(_index.Exists);
        await indexer.StopAsync();
        var next = Indexer();
        next.Start();
        Assert.False(next.IsEnabled);
        Assert.False(_index.Exists);
    }

    [Fact]
    public async Task StartingWithAnIndexCatchesUpWithHistory()
    {
        _index.Upsert([HistorySearchRecord.From(HistorySearchTestData.Record("gone", "budget"))!,
            HistorySearchRecord.From(HistorySearchTestData.Record("kept", "weather"))!]);
        _history.Replace(HistorySearchTestData.Record("kept", "weather"), HistorySearchTestData.Record("new", "holiday"));
        var indexer = Indexer();

        indexer.Start();

        Assert.True(indexer.IsEnabled);
        await Eventually(() => Indexed().SequenceEqual(["kept", "new"]));
    }

    [Fact]
    public async Task ADamagedIndexIsBuiltAgainInFull()
    {
        _history.Replace([.. Enumerable.Range(0, 120).Select(number => HistorySearchTestData.Record("r" + number, "budget " + number))]);
        var indexer = Indexer();
        await indexer.PrepareAsync();
        _index.Close();
        File.WriteAllBytes(Path.Join(_directory, HistorySearchIndex.FileName), Encoding.ASCII.GetBytes(new string('x', 8192)));

        _history.Replace([.. _history.Records, HistorySearchTestData.Record("extra", "budget")]);
        await Eventually(() => Indexed().Count == 121);
    }
}

internal static class HistorySearchTestData
{
    internal static TranscriptionRecord Record(string id, string text, long recordedAt = 1_000, string source = "dictation") => new()
    {
        Id = id, Timestamp = DateTimeOffset.FromUnixTimeSeconds(recordedAt).UtcDateTime, RawText = text, FinalText = text, SourceKind = source
    };

    // Raises RecordsChanged like History: once per saved change, on the saving thread.
    internal sealed class FakeHistory : IHistoryService
    {
        private volatile TranscriptionRecord[] _records = [];
        public IReadOnlyList<TranscriptionRecord> Records => [.. _records];
        public event Action? RecordsChanged;
        public void Replace(params TranscriptionRecord[] records)
        {
            _records = records;
            RecordsChanged?.Invoke();
        }
        public Task EnsureLoadedAsync() => Task.CompletedTask;
        public void AddRecord(TranscriptionRecord record) => throw new NotSupportedException();
        public void UpdateRecord(string id, string finalText) => throw new NotSupportedException();
        public void DeleteRecord(string id) => throw new NotSupportedException();
        public void ClearAll() => throw new NotSupportedException();
        public IReadOnlyList<TranscriptionRecord> Search(string query) => throw new NotSupportedException();
        public void PurgeOldRecords(TimeSpan? retention) => throw new NotSupportedException();
        public int TotalRecords => _records.Length;
        public int TotalWords => 0;
        public double TotalDuration => 0;
        public IReadOnlyList<string> GetDistinctApps() => [];
        public string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => throw new NotSupportedException();
        public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => throw new NotSupportedException();
        public string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => throw new NotSupportedException();
        public string ExportToJson(IReadOnlyList<TranscriptionRecord> records) => throw new NotSupportedException();
    }
}
