using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TypeWhisper.Presentation;

/// <summary>An index file that is damaged or is not an index of this format, or another SQLite error.</summary>
public sealed class HistorySearchIndexException(string message, bool isUnusableFile, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>True when the file is damaged or is not an index of this format. Other errors, such as a locked file, can pass.</summary>
    public bool IsUnusableFile { get; } = isUnusableFile;
}

/// <summary>The full-text index over History, in a SQLite file of its own in the profile.</summary>
/// <remarks>
/// It is derived data: every record in it can be made again from History, so a file that is damaged or has another
/// format is deleted and built again, and "Export all data" leaves it out. Every member is thread-safe; one connection
/// serves them one at a time.
/// </remarks>
public sealed class HistorySearchIndex : IDisposable
{
    /// <summary>The index's folder in the profile.</summary>
    public const string DirectoryName = "HistorySearch";

    /// <summary>The index file in <see cref="DirectoryName"/>.</summary>
    public const string FileName = "index.sqlite";

    /// <summary>Raise it when the tables or what they hold change. An index with another format is built again.</summary>
    public const string FormatVersion = "2";

    // Row IDs grow with the recording time, also for records that arrive late through sync. A period is then a range
    // of passage IDs, which full-text search filters far faster than a join on the records. A record's ID is its time
    // in seconds times RecordsPerSecond, plus its millisecond times RecordsPerMillisecond, plus a counter for records
    // of the same millisecond; records that do not fit their millisecond take the IDs after the last millisecond of
    // their second. A passage's ID is its record's ID times PassagesPerRecord plus its position.
    internal const long RecordsPerSecond = 1L << 16;
    private const long RecordsPerMillisecond = 64;
    private const long OverflowStart = 1000 * RecordsPerMillisecond;
    internal const long PassagesPerRecord = 1L << 14;

    // A word that more than this share of the passages holds is left out of a search: it matches so much that it costs
    // time and tells passages apart the least. Small indexes keep every word.
    private const double CommonWordShare = 0.1;
    private const int PassagesForCommonWords = 500;
    private const int TitleLimit = 20;
    private const int NewestLimit = 20;
    // Passages of one record a search keeps.
    private const int CandidatesPerRecord = 6;
    private const int SubstringLimit = 20;
    // With fewer hits than this, a search also looks for the words inside longer ones: "zahnarzt" in "kinderzahnarzt".
    private const int HitsWithoutSubstrings = 3;
    private const int SubstringLength = 5;
    // The weight of a rank when rankings are merged (reciprocal rank fusion).
    private const double RankOffset = 60;

    private const int SqliteError = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly object _gate = new();
    private readonly string _directory;
    private SqliteConnection? _connection;
    // What the last search read about the index's content. Every write clears it.
    private long? _passageCount;
    private HistorySearchVocabulary? _vocabulary;

    /// <summary>Creates the index over the folder; nothing is opened or created yet.</summary>
    /// <param name="directory">The folder that holds the index file and nothing else.</param>
    public HistorySearchIndex(string directory) => _directory = directory;

    /// <summary>Called, while the index is locked, after it deleted a file that was damaged or had another format.</summary>
    public event Action? Damaged;

    /// <summary>The folder that holds the index.</summary>
    public string Directory => _directory;

    private string FilePath => Path.Join(_directory, FileName);

    /// <summary>Whether an index file exists.</summary>
    public bool Exists
    {
        get { lock (_gate) return _connection is not null || File.Exists(FilePath); }
    }

    /// <summary>Creates an empty index when none exists.</summary>
    public void Create()
    {
        lock (_gate) Open(creating: true);
    }

    /// <summary>Adds the records, replacing what the index holds for them. Creates the index when needed.</summary>
    public void Upsert(IReadOnlyCollection<HistorySearchRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return;
        lock (_gate)
        {
            var connection = Open(creating: true)!;
            Transaction(connection, () =>
            {
                foreach (var record in records)
                {
                    Delete(connection, record.Id);
                    var rowId = FreeRowId(connection, record.RecordedAt);
                    Run(connection, """
                        INSERT INTO records(id, record_id, recorded_at, source, language, duration, title, app_name,
                          app_process, domain, content_hash)
                        VALUES(@id, @record, @at, @source, @language, @duration, @title, @app, @process, @domain, @hash)
                        """,
                        ("@id", rowId), ("@record", record.Id), ("@at", record.RecordedAt.ToUnixTimeMilliseconds() / 1000.0),
                        ("@source", record.Source), ("@language", record.Language), ("@duration", record.DurationSeconds),
                        ("@title", record.Title), ("@app", record.AppName), ("@process", record.AppProcessName),
                        ("@domain", record.Domain), ("@hash", record.ContentHash));
                    var ordinal = 0L;
                    foreach (var passage in record.Passages.Take((int)PassagesPerRecord))
                    {
                        Run(connection, """
                            INSERT INTO passages(id, record_row, ordinal, kind, text, speaker, start_seconds, end_seconds,
                              char_start, char_end)
                            VALUES(@id, @row, @ordinal, @kind, @text, @speaker, @start, @end, @charStart, @charEnd)
                            """,
                            ("@id", rowId * PassagesPerRecord + ordinal), ("@row", rowId), ("@ordinal", ordinal),
                            ("@kind", KindName(passage.Kind)), ("@text", passage.Text),
                            ("@speaker", passage.Speakers.Count == 0 ? null : string.Join('\n', passage.Speakers)),
                            ("@start", passage.StartSeconds), ("@end", passage.EndSeconds),
                            ("@charStart", passage.TextStart), ("@charEnd", passage.TextEnd));
                        ordinal++;
                    }
                }
            });
        }
    }

    /// <summary>Removes the records, and the summaries kept for them. Does nothing without an index file.</summary>
    public void Remove(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        lock (_gate)
        {
            if (Open(creating: false) is not { } connection) return;
            Transaction(connection, () => { foreach (var id in ids) Delete(connection, id); });
        }
    }

    /// <summary>
    /// Closes the index and deletes its files. Files that cannot be deleted are emptied instead. Returns false when
    /// text of History may still be in the folder.
    /// </summary>
    public bool Destroy()
    {
        lock (_gate) return DestroyFiles();
    }

    /// <summary>Closes the index file; the next use opens it again.</summary>
    public void Close()
    {
        lock (_gate) CloseConnection();
    }

    /// <summary>The content hash of every indexed record. Empty without an index file and for a damaged one, which is deleted.</summary>
    public IReadOnlyDictionary<string, string> ContentHashes() => Reading(new Dictionary<string, string>(StringComparer.Ordinal), connection =>
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, hash) in Rows(connection, "SELECT record_id, content_hash FROM records", row => (row.GetString(0), row.GetString(1))))
            hashes.TryAdd(id, hash);
        return hashes;
    });

    /// <summary>How many records and passages the index holds.</summary>
    public (int Records, int Passages) Counts() => Reading((0, 0), connection =>
        ((int)Scalar(connection, "SELECT count(*) FROM records"), (int)Scalar(connection, "SELECT count(*) FROM passages")));

    /// <summary>Throws when the full-text tables no longer match the stored passages and titles.</summary>
    public void CheckIntegrity()
    {
        lock (_gate)
        {
            if (Open(creating: false) is not { } connection) return;
            try
            {
                // Without a rank of 1, FTS5 only checks the full-text tables against themselves.
                Run(connection, "INSERT INTO passages_fts(passages_fts, rank) VALUES('integrity-check', 1)");
                Run(connection, "INSERT INTO titles_fts(titles_fts, rank) VALUES('integrity-check', 1)");
            }
            catch (SqliteException ex) { throw Wrap(ex); }
        }
    }

    /// <summary>
    /// The passages that match the words and names best, best first. A record whose title matches adds its best
    /// passage. Empty without an index and for a damaged one, which is deleted.
    /// </summary>
    /// <param name="words">Words to find; each also finds longer words that start with it.</param>
    /// <param name="names">Names to find as they are written.</param>
    /// <param name="scope">The part of History to look at.</param>
    /// <param name="limit">The most passages to return.</param>
    /// <param name="includeNewest">Also the newest matching passages, which may rank too low to be among the best.</param>
    public IReadOnlyList<HistorySearchHit> Search(IEnumerable<string> words, IEnumerable<string>? names = null,
        HistorySearchScope? scope = null, int limit = 60, bool includeNewest = false)
    {
        ArgumentNullException.ThrowIfNull(words);
        var nameList = (names ?? []).ToArray();
        var wordList = HistoryFullTextQuery.Words(string.Join(' ', words));
        scope ??= new();
        if (limit <= 0) return [];
        return Reading<IReadOnlyList<HistorySearchHit>>([], connection =>
        {
            var searched = WithoutCommonWords(connection, wordList, keepingAll: nameList.Length == 0);
            if (HistoryFullTextQuery.Expression(searched, nameList) is not { } expression) return [];
            var records = RecordIds(scope);
            var passages = PassageIds(records);
            var filter = FilterValues(scope);
            var filtered = scope.LimitsRecords ? $"AND rowid / {PassagesPerRecord} IN {RecordFilter}" : "";
            // At most a few passages of each record, so one long recording cannot take every place.
            string Matching(string order) => $"""
                SELECT id FROM (
                    SELECT id, score, row_number() OVER (PARTITION BY id / {PassagesPerRecord} ORDER BY score) AS place
                    FROM (
                        SELECT rowid AS id, {order} AS score FROM passages_fts
                        WHERE passages_fts MATCH @query AND rowid BETWEEN @low AND @high {filtered}
                    )
                )
                WHERE place <= {CandidatesPerRecord}
                ORDER BY score LIMIT @limit
                """;
            (string, object?)[] Bounded(long low, long high, long count) =>
                [("@query", expression), ("@low", low), ("@high", high), ("@limit", count), .. filter];

            var found = Rows(connection, Matching("bm25(passages_fts, 1.0, 3.0)"), row => row.GetInt64(0),
                Bounded(passages.Low, passages.High, limit));
            var scores = new Dictionary<long, double>();
            var order = new List<long>();
            void Add(long passage, int rank)
            {
                if (!scores.ContainsKey(passage)) order.Add(passage);
                scores[passage] = scores.GetValueOrDefault(passage) + 1 / (RankOffset + rank);
            }
            for (var index = 0; index < found.Count; index++) Add(found[index], index + 1);
            if (includeNewest)
            {
                var newest = Rows(connection, Matching("-rowid"), row => row.GetInt64(0), Bounded(passages.Low, passages.High, NewestLimit));
                for (var index = 0; index < newest.Count; index++) Add(newest[index], index + 1);
            }

            List<long> Titled(string titleOrder, int count) => Rows(connection, $"""
                SELECT rowid FROM titles_fts
                WHERE titles_fts MATCH @query AND rowid BETWEEN @low AND @high
                {(scope.LimitsRecords ? $"AND rowid IN {RecordFilter}" : "")}
                ORDER BY {titleOrder} LIMIT @limit
                """, row => row.GetInt64(0), Bounded(records.Low, records.High, count));
            var titleRankings = new List<List<long>> { Titled("bm25(titles_fts)", TitleLimit) };
            if (includeNewest) titleRankings.Add(Titled("rowid DESC", NewestLimit));
            foreach (var ranking in titleRankings)
            {
                for (var index = 0; index < ranking.Count; index++)
                {
                    var own = PassageIds((ranking[index], ranking[index]));
                    // The record's best passage: one already found, the best match further down, or its first one.
                    var best = found.FirstOrDefault(passage => passage >= own.Low && passage <= own.High, -1);
                    if (best < 0)
                    {
                        var match = Rows(connection, """
                            SELECT rowid FROM passages_fts
                            WHERE passages_fts MATCH @query AND rowid BETWEEN @low AND @high
                            ORDER BY bm25(passages_fts, 1.0, 3.0) LIMIT 1
                            """, row => row.GetInt64(0), ("@query", expression), ("@low", own.Low), ("@high", own.High));
                        best = match.Count > 0 ? match[0] : own.Low;
                    }
                    Add(best, index + 1);
                }
            }

            if (order.Count < HitsWithoutSubstrings)
            {
                foreach (var word in searched.Where(word => word.Length >= SubstringLength))
                {
                    var containing = Rows(connection, $"""
                        SELECT id FROM (
                            SELECT id, row_number() OVER (PARTITION BY record_row ORDER BY id DESC) AS place
                            FROM passages
                            WHERE id BETWEEN @low AND @high AND instr(history_fold(text), @word) > 0
                            {(scope.LimitsRecords ? $"AND record_row IN {RecordFilter}" : "")}
                        )
                        WHERE place <= {CandidatesPerRecord}
                        ORDER BY id DESC LIMIT @limit
                        """, row => row.GetInt64(0),
                        [("@word", HistoryFullTextQuery.Folded(word)), ("@low", passages.Low), ("@high", passages.High),
                         ("@limit", (long)SubstringLimit), .. filter]);
                    foreach (var passage in containing.Where(passage => !scores.ContainsKey(passage))) Add(passage, order.Count + 1);
                }
            }

            return order.Select((passage, position) => (passage, position))
                .OrderByDescending(item => scores[item.passage]).ThenBy(item => item.position)
                .Take(limit)
                .Select(item => Hit(connection, item.passage) is { } hit ? hit with { Score = scores[item.passage] } : null)
                .OfType<HistorySearchHit>().ToArray();
        });
    }

    /// <summary>The newest records of the scope, each with its first passage, and the number of records the scope holds.</summary>
    public (IReadOnlyList<HistorySearchHit> Hits, int Total) NewestRecords(HistorySearchScope scope, int limit)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Reading<(IReadOnlyList<HistorySearchHit>, int)>(([], 0), connection =>
        {
            var records = RecordIds(scope);
            var filter = FilterValues(scope);
            // Records without text have no passage and are left out.
            var scoped = $"""
                id BETWEEN @low AND @high
                {(scope.LimitsRecords ? $"AND id IN {RecordFilter}" : "")}
                AND EXISTS (SELECT 1 FROM passages WHERE passages.id = records.id * {PassagesPerRecord})
                """;
            var found = limit <= 0 ? new List<long>() : Rows(connection, $"SELECT id FROM records WHERE {scoped} ORDER BY recorded_at DESC, id DESC LIMIT @limit",
                row => row.GetInt64(0), [("@low", records.Low), ("@high", records.High), ("@limit", (long)limit), .. filter]);
            var total = Scalar(connection, $"SELECT count(*) FROM records WHERE {scoped}", [("@low", records.Low), ("@high", records.High), .. filter]);
            return (found.Select(record => Hit(connection, record * PassagesPerRecord)).OfType<HistorySearchHit>().ToArray(), (int)total);
        });
    }

    /// <summary>
    /// The text of a record as the index holds it: what was said, in order, without a text written from it, and the
    /// hash of the content it comes from. Null for a record the index does not hold.
    /// </summary>
    public (IReadOnlyList<HistoryPassage> Passages, string ContentHash)? Transcript(string recordId)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        return Reading<(IReadOnlyList<HistoryPassage>, string)?>(null, connection =>
        {
            var hashes = Rows(connection, "SELECT content_hash FROM records WHERE record_id = @record", row => row.GetString(0), ("@record", recordId));
            if (hashes.Count == 0) return null;
            var passages = Rows(connection, """
                SELECT p.id FROM passages p JOIN records r ON r.id = p.record_row
                WHERE r.record_id = @record AND p.kind = @kind ORDER BY p.id
                """, row => row.GetInt64(0), ("@record", recordId), ("@kind", KindName(HistoryPassageKind.Transcript)))
                .Select(id => Hit(connection, id)?.Passage).OfType<HistoryPassage>().ToArray();
            return (passages, hashes[0]);
        });
    }

    /// <summary>The summary kept for a record in a language and the provider that wrote it, unless the record changed since or is gone.</summary>
    /// <param name="recordId">The record's identifier.</param>
    /// <param name="language">An ISO 639 code, or empty for the recording's own language.</param>
    public (string Text, string ProviderId)? Summary(string recordId, string language = "")
    {
        ArgumentNullException.ThrowIfNull(recordId);
        return Reading<(string, string)?>(null, connection =>
        {
            var rows = Rows(connection, """
                SELECT s.summary, s.provider_id FROM record_summaries s JOIN records r ON r.record_id = s.record_id
                WHERE s.record_id = @record AND s.language = @language AND s.content_hash = r.content_hash
                """, row => (row.GetString(0), row.GetString(1)), ("@record", recordId), ("@language", language ?? ""));
            return rows.Count == 0 ? null : rows[0];
        });
    }

    /// <summary>
    /// Keeps a summary of a record until the record changes or is removed. Does nothing when the record is gone or no
    /// longer has the content the summary was written from, which can change while a model writes it.
    /// </summary>
    public void SetSummary(string recordId, string summary, string providerId, string contentHash, string language = "")
    {
        ArgumentNullException.ThrowIfNull(recordId);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(contentHash);
        lock (_gate)
        {
            if (Open(creating: false) is not { } connection) return;
            Transaction(connection, () => Run(connection, """
                INSERT OR REPLACE INTO record_summaries(record_id, language, content_hash, summary, provider_id)
                SELECT record_id, @language, content_hash, @summary, @provider FROM records
                WHERE record_id = @record AND content_hash = @hash
                """, ("@record", recordId), ("@summary", summary), ("@provider", providerId), ("@hash", contentHash),
                ("@language", language ?? "")));
        }
    }

    /// <summary>The speakers, apps and domains of the indexed records.</summary>
    /// <param name="recordIds">The records to take names from, or null for every indexed record.</param>
    public HistorySearchVocabulary Vocabulary(IReadOnlySet<string>? recordIds = null)
    {
        lock (_gate)
        {
            if (recordIds is null && _vocabulary is { } cached) return cached;
            var only = recordIds is null ? "" : "AND r.record_id IN (SELECT value FROM json_each(@ids))";
            (string, object?)[] values = recordIds is null ? [] : [("@ids", JsonSerializer.Serialize(recordIds.Order(StringComparer.Ordinal)))];
            var vocabulary = Reading(HistorySearchVocabulary.Empty, connection =>
            {
                var speakers = Rows(connection, $"""
                    SELECT DISTINCT p.speaker FROM passages p JOIN records r ON r.id = p.record_row
                    WHERE p.speaker IS NOT NULL {only}
                    """, row => row.GetString(0), values)
                    .SelectMany(names => names.Split('\n')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var apps = Rows(connection, $"""
                    SELECT DISTINCT r.app_name, r.app_process FROM records r
                    WHERE r.app_name IS NOT NULL AND r.app_process IS NOT NULL {only}
                    ORDER BY r.app_name, r.app_process
                    """, row => new HistorySearchApp(row.GetString(0), row.GetString(1)), values).ToArray();
                var domains = Rows(connection, $"SELECT DISTINCT r.domain FROM records r WHERE r.domain IS NOT NULL {only} ORDER BY r.domain",
                    row => row.GetString(0), values).ToArray();
                return new HistorySearchVocabulary(speakers, apps, domains);
            });
            if (recordIds is null && _connection is not null) _vocabulary = vocabulary;
            return vocabulary;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Close();

    // Runs a query. Without an index file, and for a damaged one, which is deleted, the result is `empty`.
    private T Reading<T>(T empty, Func<SqliteConnection, T> body)
    {
        lock (_gate)
        {
            if (Open(creating: false) is not { } connection) return empty;
            try { return body(connection); }
            catch (SqliteException ex) when (IsUnusable(ex))
            {
                DestroyDamagedFile();
                return empty;
            }
            catch (SqliteException ex) { throw Wrap(ex); }
        }
    }

    private List<string> WithoutCommonWords(SqliteConnection connection, IReadOnlyList<string> words, bool keepingAll)
    {
        // The full-text table's row sizes: one small row per passage.
        var total = _passageCount ??= Scalar(connection, "SELECT count(*) FROM passages_fts_docsize");
        if (total < PassagesForCommonWords) return [.. words];
        Run(connection, "CREATE VIRTUAL TABLE IF NOT EXISTS temp.passage_words USING fts5vocab(main, passages_fts, row)");
        var maximum = (long)(CommonWordShare * total);
        var rare = words.Where(word =>
        {
            // As the index stores the word. A longer word also counts those that start with it.
            var stored = HistoryFullTextQuery.Folded(word);
            var isPrefix = new StringInfo(word).LengthInTextElements >= HistoryFullTextQuery.PrefixLength;
            var sum = Scalar(connection, "SELECT COALESCE(SUM(doc), 0) FROM temp.passage_words WHERE term >= @low AND term <= @high",
                ("@low", stored), ("@high", isPrefix ? stored + "\U0010FFFF" : stored));
            if (!isPrefix || sum <= maximum) return sum <= maximum;
            // The sum counts a passage once for every word with the prefix it holds: "budget budgets". Counted up to
            // the share, each passage counts once.
            var passages = Scalar(connection, "SELECT count(*) FROM (SELECT rowid FROM passages_fts WHERE passages_fts MATCH @query LIMIT @limit)",
                ("@query", "\"" + stored.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"), ("@limit", maximum + 1));
            return passages <= maximum;
        }).ToList();
        // A search made of common words only still looks for them.
        return rare.Count == 0 && keepingAll ? [.. words] : rare;
    }

    // The IDs that records of the period can have: whole seconds, which also cover records that did not fit their
    // millisecond. RecordFilter then checks the recording time exactly. A period that ends before it starts holds none.
    private static (long Low, long High) RecordIds(HistorySearchScope scope)
    {
        var low = scope.PeriodStart is { } start ? Second(start.ToUnixTimeMilliseconds()) * RecordsPerSecond : 0;
        var high = scope.PeriodEnd is { } end && end > (scope.PeriodStart ?? DateTimeOffset.MinValue)
            ? (Second(end.ToUnixTimeMilliseconds() - 1) + 1) * RecordsPerSecond - 1
            : scope.PeriodEnd is null ? (long)uint.MaxValue * RecordsPerSecond + RecordsPerSecond - 1 : low - 1;
        return (low, high);
    }

    private static long Second(long milliseconds) => milliseconds <= 0 ? 0 : Math.Min(milliseconds / 1000, uint.MaxValue);

    // The first row ID of a moment's millisecond; times before 1970 share the first one.
    private static long RowBase(long milliseconds)
    {
        if (milliseconds <= 0) return 0;
        var second = Math.Min(milliseconds / 1000, uint.MaxValue);
        return second * RecordsPerSecond + (second == uint.MaxValue ? 0 : milliseconds % 1000) * RecordsPerMillisecond;
    }

    private static (long Low, long High) PassageIds((long Low, long High) records) =>
        (records.Low * PassagesPerRecord, records.High * PassagesPerRecord + PassagesPerRecord - 1);

    // The records of a scope's sources, apps, domains and identifiers, as a subquery over FilterValues.
    // The row-ID range narrows the records quickly; the recording time then decides the period exactly.
    private const string RecordFilter = """
        (SELECT id FROM records
         WHERE id BETWEEN @recordLow AND @recordHigh
           AND (@from IS NULL OR recorded_at >= @from) AND (@until IS NULL OR recorded_at < @until)
           AND (@sources IS NULL OR source IN (SELECT value FROM json_each(@sources)))
           AND (@apps IS NULL OR app_process IN (SELECT value FROM json_each(@apps)))
           AND (@domains IS NULL OR domain IN (SELECT value FROM json_each(@domains)))
           AND (@ids IS NULL OR record_id IN (SELECT value FROM json_each(@ids))))
        """;

    private static (string, object?)[] FilterValues(HistorySearchScope scope)
    {
        if (!scope.LimitsRecords) return [];
        static string? Json(IReadOnlySet<string>? values, bool emptyLimits) =>
            values is null || values.Count == 0 && !emptyLimits ? null : JsonSerializer.Serialize(values.Order(StringComparer.Ordinal));
        // Process names and domains are stored in lowercase, so they compare without case.
        var apps = scope.AppProcessNames.Select(HistorySearchRecord.NormalizedProcessName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var domains = scope.Domains.Select(HistorySearchRecord.NormalizedHost).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // A limit whose every value normalizes away still limits: it matches none.
        string? Limit(IReadOnlySet<string> requested, HashSet<string> normalized) =>
            requested.Count == 0 ? null : Json(normalized.Count == 0 ? new HashSet<string> { "" } : normalized, true);
        var (recordLow, recordHigh) = RecordIds(scope);
        // As the records store their time: whole milliseconds in seconds.
        static object? Seconds(DateTimeOffset? moment) => moment is { } value ? value.ToUnixTimeMilliseconds() / 1000.0 : null;
        return [("@recordLow", recordLow), ("@recordHigh", recordHigh), ("@from", Seconds(scope.PeriodStart)), ("@until", Seconds(scope.PeriodEnd)),
            ("@sources", Json(scope.Sources, false)), ("@apps", Limit(scope.AppProcessNames, apps)), ("@domains", Limit(scope.Domains, domains)),
            // An empty set of records matches none.
            ("@ids", Json(scope.RecordIds, true))];
    }

    private static HistorySearchHit? Hit(SqliteConnection connection, long passageId)
    {
        var hits = Rows(connection, """
            SELECT p.ordinal, p.kind, p.text, p.speaker, p.start_seconds, p.end_seconds, p.char_start, p.char_end,
                   r.record_id, r.recorded_at, r.source, r.title, r.app_name, r.domain
            FROM passages p JOIN records r ON r.id = p.record_row
            WHERE p.id = @id
            """, HistorySearchHit? (row) =>
        {
            if (KindFrom(row.GetString(1)) is not { } kind) return null;
            var ordinal = (int)row.GetInt64(0);
            int? start = row.IsDBNull(6) ? null : (int)row.GetInt64(6);
            int? end = row.IsDBNull(7) ? null : (int)row.GetInt64(7);
            if (start is null || end is null || start > end) start = end = null;
            var speakers = row.IsDBNull(3) ? [] : row.GetString(3).Split('\n');
            var passage = new HistoryPassage(kind, row.GetString(2), speakers,
                row.IsDBNull(4) ? null : row.GetDouble(4), row.IsDBNull(5) ? null : row.GetDouble(5), start, end);
            var recordedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(row.GetDouble(9) * 1000));
            return new HistorySearchHit(row.GetString(8), recordedAt, row.GetString(10),
                row.IsDBNull(11) ? null : row.GetString(11), row.IsDBNull(12) ? null : row.GetString(12),
                row.IsDBNull(13) ? null : row.GetString(13), ordinal, ordinal, passage);
        }, ("@id", passageId));
        return hits.Count > 0 ? hits[0] : null;
    }

    private static string KindName(HistoryPassageKind kind) => kind == HistoryPassageKind.Result ? "result" : "transcript";

    private static HistoryPassageKind? KindFrom(string name) => name switch
    {
        "transcript" => HistoryPassageKind.Transcript,
        "result" => HistoryPassageKind.Result,
        _ => null
    };


    // Rows

    private static void Delete(SqliteConnection connection, string id)
    {
        foreach (var rowId in Rows(connection, "SELECT id FROM records WHERE record_id = @record", row => row.GetInt64(0), ("@record", id)))
        {
            Run(connection, "DELETE FROM passages WHERE id BETWEEN @low AND @high",
                ("@low", rowId * PassagesPerRecord), ("@high", (rowId + 1) * PassagesPerRecord - 1));
            Run(connection, "DELETE FROM records WHERE id = @id", ("@id", rowId));
        }
        // A summary belongs to the text it was made from.
        Run(connection, "DELETE FROM record_summaries WHERE record_id = @record", ("@record", id));
    }

    private static long FreeRowId(SqliteConnection connection, DateTimeOffset recordedAt)
    {
        var first = RowBase(recordedAt.ToUnixTimeMilliseconds());
        var second = first - first % RecordsPerSecond;
        // The record's millisecond, then the IDs its second keeps for full milliseconds, then the next seconds.
        foreach (var (low, high) in new[] { (first, first + RecordsPerMillisecond - 1), (second + OverflowStart, second + RecordsPerSecond - 1) })
            if (Next(low, high) is { } free) return free;
        for (second += RecordsPerSecond; ; second += RecordsPerSecond)
            if (Next(second + OverflowStart, second + RecordsPerSecond - 1) is { } free) return free;

        long? Next(long low, long high)
        {
            var next = Scalar(connection, "SELECT COALESCE(MAX(id) + 1, @first) FROM records WHERE id BETWEEN @first AND @last",
                ("@first", low), ("@last", high));
            return next <= high ? next : null;
        }
    }

    private void Transaction(SqliteConnection connection, Action body)
    {
        _passageCount = null;
        _vocabulary = null;
        try
        {
            Run(connection, "BEGIN IMMEDIATE");
            try
            {
                body();
                Run(connection, "COMMIT");
            }
            catch
            {
                try { Run(connection, "ROLLBACK"); }
                catch (SqliteException)
                {
                    // SQLite already rolled back after the error that ended the transaction; the original error is the one to report.
                }
                throw;
            }
        }
        catch (SqliteException ex)
        {
            if (IsUnusable(ex)) DestroyDamagedFile();
            throw Wrap(ex);
        }
    }

    // File

    private SqliteConnection? Open(bool creating)
    {
        if (_connection is not null) return _connection;
        var existed = File.Exists(FilePath);
        if (!creating && !existed) return null;
        try { return _connection = OpenFile(); }
        catch (Exception ex) when (existed && IsUnusable(ex))
        {
            DestroyDamagedFile();
            return creating ? _connection = OpenFile() : null;
        }
    }

    private SqliteConnection OpenFile()
    {
        System.IO.Directory.CreateDirectory(_directory);
        // Without pooling, closing the connection closes the file, so the folder can be deleted right afterwards.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            // Deleted text is overwritten in the file, not only marked free.
            Run(connection, "PRAGMA secure_delete=ON; PRAGMA journal_mode=DELETE; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            // Text as a word search compares it: lowercased, composed, without accents, as the tokenizer folds it.
            connection.CreateFunction("history_fold", (string? text) => text is null ? null : HistoryFullTextQuery.Folded(text.ToLowerInvariant()),
                isDeterministic: true);
            string? format;
            var hasMeta = true;
            try { format = Rows(connection, "SELECT value FROM index_meta WHERE key = 'format'", row => row.GetString(0)).FirstOrDefault(); }
            catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) == SqliteError)
            {
                // No such table: a new file. Other errors, such as a locked file, pass.
                format = null;
                hasMeta = false;
            }
            if (hasMeta)
            {
                if (format != FormatVersion) throw new HistorySearchIndexException($"Index format {format ?? "?"}.", isUnusableFile: true);
            }
            else
            {
                try { Run(connection, Schema); }
                catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) == SqliteError)
                {
                    // A database with other tables in the way.
                    throw new HistorySearchIndexException("Not a search index.", isUnusableFile: true, ex);
                }
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void DestroyDamagedFile()
    {
        DestroyFiles();
        Damaged?.Invoke();
    }

    private bool DestroyFiles()
    {
        CloseConnection();
        try { System.IO.Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still in use or protected stays; it is emptied below instead.
        }
        if (!System.IO.Directory.Exists(_directory)) return true;
        // Emptying a file needs no right to change its folder.
        try
        {
            var empty = true;
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.SetLength(0);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { empty = false; }
            }
            return empty;
        }
        // A folder that cannot be listed may still hold the files.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void CloseConnection()
    {
        _passageCount = null;
        _vocabulary = null;
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }

    private static bool IsUnusable(Exception ex) => ex switch
    {
        HistorySearchIndexException index => index.IsUnusableFile,
        SqliteException sqlite => (sqlite.SqliteErrorCode & 0xFF) is SqliteCorrupt or SqliteNotADatabase,
        _ => false
    };

    private static HistorySearchIndexException Wrap(SqliteException ex) =>
        new($"SQLite error {ex.SqliteErrorCode}: {ex.Message}", IsUnusable(ex), ex);

    // Commands

    private static SqliteCommand Command(SqliteConnection connection, string sql, IEnumerable<(string Name, object? Value)> values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Run(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, sql, values);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, sql, values);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, sql, values);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    private const string Schema = $"""
        BEGIN;
        CREATE TABLE records(
          id INTEGER PRIMARY KEY,
          record_id TEXT NOT NULL UNIQUE,
          recorded_at REAL NOT NULL,
          source TEXT NOT NULL,
          language TEXT,
          duration REAL NOT NULL,
          title TEXT,
          app_name TEXT,
          app_process TEXT,
          domain TEXT,
          content_hash TEXT NOT NULL
        );
        CREATE TABLE passages(
          id INTEGER PRIMARY KEY,
          record_row INTEGER NOT NULL,
          ordinal INTEGER NOT NULL,
          kind TEXT NOT NULL,
          text TEXT NOT NULL,
          speaker TEXT,
          start_seconds REAL,
          end_seconds REAL,
          char_start INTEGER,
          char_end INTEGER
        );
        CREATE TABLE record_summaries(
          record_id TEXT NOT NULL,
          language TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          summary TEXT NOT NULL,
          provider_id TEXT NOT NULL,
          PRIMARY KEY(record_id, language)
        ) WITHOUT ROWID;
        CREATE VIRTUAL TABLE passages_fts USING fts5(
          text, speaker,
          content='passages', content_rowid='id',
          tokenize='unicode61 remove_diacritics 2'
        );
        CREATE VIRTUAL TABLE titles_fts USING fts5(
          title,
          content='records', content_rowid='id',
          tokenize='unicode61 remove_diacritics 2'
        );
        INSERT INTO passages_fts(passages_fts, rank) VALUES('secure-delete', 1);
        INSERT INTO titles_fts(titles_fts, rank) VALUES('secure-delete', 1);
        CREATE TRIGGER passages_inserted AFTER INSERT ON passages BEGIN
          INSERT INTO passages_fts(rowid, text, speaker) VALUES(new.id, new.text, new.speaker);
        END;
        CREATE TRIGGER passages_deleted AFTER DELETE ON passages BEGIN
          INSERT INTO passages_fts(passages_fts, rowid, text, speaker) VALUES('delete', old.id, old.text, old.speaker);
        END;
        CREATE TRIGGER records_inserted AFTER INSERT ON records BEGIN
          INSERT INTO titles_fts(rowid, title) VALUES(new.id, new.title);
        END;
        CREATE TRIGGER records_deleted AFTER DELETE ON records BEGIN
          INSERT INTO titles_fts(titles_fts, rowid, title) VALUES('delete', old.id, old.title);
        END;
        CREATE TABLE index_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        INSERT INTO index_meta(key, value) VALUES('format', '{FormatVersion}');
        COMMIT;
        """;
}
