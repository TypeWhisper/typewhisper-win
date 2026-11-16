using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>What the History search index stores about one History record.</summary>
/// <param name="Id">The record's identifier.</param>
/// <param name="RecordedAt">When the record was made.</param>
/// <param name="Source">Where the record came from, such as <c>"dictation"</c> or <c>"file"</c>.</param>
/// <param name="Language">The language code, or null.</param>
/// <param name="DurationSeconds">The length of the audio.</param>
/// <param name="Title">The name of a recording from another device, or null.</param>
/// <param name="AppName">The app a dictation was made in, or null.</param>
/// <param name="AppProcessName">The process name of that app, or null.</param>
/// <param name="Domain">The website domain a dictation was made on, or null.</param>
/// <param name="ContentHash">A hash of everything the index takes from the record.</param>
/// <param name="Passages">The record's passages in order.</param>
public sealed record HistorySearchRecord(
    string Id,
    DateTimeOffset RecordedAt,
    string Source,
    string? Language,
    double DurationSeconds,
    string? Title,
    string? AppName,
    string? AppProcessName,
    string? Domain,
    string ContentHash,
    IReadOnlyList<HistoryPassage> Passages)
{
    /// <summary>The share of the spoken words a visible text must keep to count as the same text after corrections.</summary>
    private const double SameTextWordShare = 0.7;

    // Records are immutable and History replaces a record when it changes, so a hash computed once per instance stays valid.
    private static readonly ConditionalWeakTable<TranscriptionRecord, string> Hashes = new();

    /// <summary>
    /// Whether the index holds a record: complete, not failed and not still arriving from another device. The same
    /// records modules can read.
    /// </summary>
    public static bool Indexes(TranscriptionRecord record) =>
        record.Status == TranscriptionRecordStatus.Succeeded && record.ProcessingState is null or "ready";

    /// <summary>Where a record came from, as modules see it.</summary>
    public static string SourceOf(TranscriptionRecord record) => string.IsNullOrEmpty(record.SourceKind) ? "dictation" : record.SourceKind;

    /// <summary>History stores UTC; a timestamp read back without a kind is UTC too.</summary>
    public static DateTimeOffset RecordedAtOf(DateTime timestamp) => timestamp.Kind switch
    {
        DateTimeKind.Local => new DateTimeOffset(timestamp.ToUniversalTime(), TimeSpan.Zero),
        _ => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc), TimeSpan.Zero)
    };

    /// <summary>The website domain of a URL, lowercased and without <c>www.</c>, or null.</summary>
    public static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>The record as the index stores it, or null for a record the index does not hold.</summary>
    public static HistorySearchRecord? From(TranscriptionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Indexes(record)) return null;
        var processName = Blank(record.AppProcessName);
        var name = Blank(record.AppName);
        return new(
            record.Id,
            RecordedAtOf(record.Timestamp),
            SourceOf(record),
            Blank(record.Language),
            double.IsFinite(record.DurationSeconds) ? Math.Max(0, record.DurationSeconds) : 0,
            // A record from another device keeps a recording's name where a dictation keeps the app it was made in.
            processName is null ? name : null,
            processName is null ? null : name,
            processName,
            DomainOf(record.AppUrl),
            HashOf(record),
            PassagesOf(record));
    }

    /// <summary>A hash of everything the index takes from a record, cached per record instance.</summary>
    public static string HashOf(TranscriptionRecord record) => Hashes.GetValue(record, Compute);

    private static string Compute(TranscriptionRecord record)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var text in new[]
        {
            record.RawText, record.FinalText, record.AppName, record.AppProcessName, record.AppUrl, record.SourceKind,
            record.Language, record.DurationSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            RecordedAtOf(record.Timestamp).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            HistoryPassageChunker.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        })
        {
            var bytes = text is null ? [] : Encoding.UTF8.GetBytes(text);
            // The length keeps neighbouring fields apart; a missing field differs from an empty one.
            hash.AppendData(BitConverter.GetBytes(text is null ? -1L : bytes.LongLength));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
    }

    // What was said, and a text written from it when the visible text is no longer the spoken one: an email, a
    // translation or notes.
    private static IReadOnlyList<HistoryPassage> PassagesOf(TranscriptionRecord record)
    {
        var spoken = record.RawText ?? "";
        var visible = record.DisplayText ?? "";
        if (IsSameText(spoken, visible)) return HistoryPassageChunker.Passages(visible);
        return [.. HistoryPassageChunker.Passages(spoken), .. HistoryPassageChunker.Passages(visible, HistoryPassageKind.Result)];
    }

    internal static bool IsSameText(string spoken, string visible)
    {
        if (spoken == visible) return true;
        var spokenWords = HistoryFullTextQuery.Words(spoken);
        var visibleWords = HistoryFullTextQuery.Words(visible).ToHashSet(StringComparer.Ordinal);
        return spokenWords.Count(visibleWords.Contains) >= SameTextWordShare * spokenWords.Count;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The part of History a search looks at. An empty set does not limit it.</summary>
/// <param name="PeriodStart">The first moment of the period, or null for any time.</param>
/// <param name="PeriodEnd">The moment the period ends, or null for any time.</param>
public sealed record HistorySearchScope(DateTimeOffset? PeriodStart = null, DateTimeOffset? PeriodEnd = null)
{
    /// <summary>Record sources.</summary>
    public IReadOnlySet<string> Sources { get; init; } = new HashSet<string>();
    /// <summary>App process names.</summary>
    public IReadOnlySet<string> AppProcessNames { get; init; } = new HashSet<string>();
    /// <summary>Website domains.</summary>
    public IReadOnlySet<string> Domains { get; init; } = new HashSet<string>();
    /// <summary>The records a search is limited to, or null for no such limit. An empty set matches none.</summary>
    public IReadOnlySet<string>? RecordIds { get; init; }

    internal bool LimitsRecords => Sources.Count > 0 || AppProcessNames.Count > 0 || Domains.Count > 0 || RecordIds is not null;
}

/// <summary>A passage that a search found, with what a list of sources shows of its record.</summary>
public sealed record HistorySearchHit(
    string RecordId,
    DateTimeOffset RecordedAt,
    string Source,
    string? Title,
    string? AppName,
    string? Domain,
    int FirstOrdinal,
    int LastOrdinal,
    HistoryPassage Passage,
    double Score = 0);

/// <summary>An app records were made in.</summary>
public sealed record HistorySearchApp(string Name, string ProcessName);

/// <summary>The names in the index that a search can refer to.</summary>
public sealed record HistorySearchVocabulary(IReadOnlyList<string> Speakers, IReadOnlyList<HistorySearchApp> Apps, IReadOnlyList<string> Domains)
{
    /// <summary>A vocabulary without names.</summary>
    public static HistorySearchVocabulary Empty { get; } = new([], [], []);
}
