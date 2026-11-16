using System.Globalization;
using System.Text;

namespace TypeWhisper.ModuleKit;

/// <summary>Searches the host's full-text index of History.</summary>
/// <remarks>
/// The host keeps the index in step with History: added and changed records are indexed in the background, removed
/// records leave it at once, and clearing History empties it. Reads only return records whose content the index holds
/// as History has it now: a removed record, or a changed one that is not indexed again yet, is left out. The index
/// is off until a module calls <see cref="PrepareAsync"/>; it then stays on across restarts until
/// <see cref="DisableAsync"/> or "Delete all data". While it is off, reads find nothing.
/// </remarks>
public interface IHistorySearching
{
    /// <summary>Turns the index on and waits until it holds History as it is now.</summary>
    /// <param name="cancellationToken">Stops waiting; the index keeps catching up in the background.</param>
    /// <exception cref="InvalidOperationException">
    /// The index could not catch up, because History could not be read or the index could not be written. It tries again
    /// with the next History change; reads meanwhile may miss records.
    /// </exception>
    Task PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the index off and deletes it. Reads find nothing until <see cref="PrepareAsync"/>.</summary>
    Task DisableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The passages that match the query best, best first. A record whose title matches adds its best passage.
    /// </summary>
    Task<IReadOnlyList<ModuleHistorySearchHit>> SearchAsync(ModuleHistorySearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>The newest records of the scope, each with its first passage, and the number of records the scope holds.</summary>
    /// <param name="scope">The part of History to look at.</param>
    /// <param name="limit">The most records to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ModuleHistoryNewestRecords> NewestRecordsAsync(ModuleHistorySearchScope scope, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// What was said in a record, in order, without a text written from it, and the hash of the content it comes from.
    /// Null for a record the index does not hold.
    /// </summary>
    Task<ModuleHistoryTranscript?> TranscriptAsync(string recordId, CancellationToken cancellationToken = default);

    /// <summary>The summary kept for a record in a language, unless the record changed since or is gone.</summary>
    /// <param name="recordId">The record's identifier.</param>
    /// <param name="language">An ISO 639 code, or empty for the recording's own language.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ModuleHistorySummary?> SummaryAsync(string recordId, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps a summary of a record with the index until the record changes or is removed. Does nothing when the
    /// record no longer has the content of <paramref name="contentHash"/>.
    /// </summary>
    /// <param name="recordId">The record's identifier.</param>
    /// <param name="summary">The summary text.</param>
    /// <param name="providerId">The provider that wrote it.</param>
    /// <param name="contentHash">The <see cref="ModuleHistoryTranscript.ContentHash"/> the summary was written from.</param>
    /// <param name="language">An ISO 639 code, or empty for the recording's own language.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetSummaryAsync(string recordId, string summary, string providerId, string contentHash, string language,
        CancellationToken cancellationToken = default);

    /// <summary>The speakers, apps and domains of the indexed records.</summary>
    Task<ModuleHistoryVocabulary> VocabularyAsync(CancellationToken cancellationToken = default);
}

/// <summary>A full-text search of History.</summary>
/// <remarks>
/// Only the letters and digits of <see cref="Words"/> and <see cref="Names"/> reach the index, so operators of the
/// query language in them have no effect.
/// </remarks>
/// <param name="Words">Words to find. Each word also finds the words that start with it once it is long enough.</param>
public sealed record ModuleHistorySearchQuery(IReadOnlyList<string> Words)
{
    /// <summary>Names, searched as they are written; several words as one phrase.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>The part of History the search looks at.</summary>
    public ModuleHistorySearchScope Scope { get; init; } = new();

    /// <summary>The most passages to return.</summary>
    public int Limit { get; init; } = 60;

    /// <summary>Also the newest matching passages, which may rank too low to be among the best.</summary>
    public bool IncludeNewest { get; init; }

    /// <summary>
    /// The words of a text as the index splits them, lowercased and composed: letters and digits, with the accents and
    /// other combining marks that follow them.
    /// </summary>
    public static IReadOnlyList<string> WordsIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized;
        try { normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant(); }
        // A lone surrogate cannot be normalized; the index reads such text as it is too.
        catch (ArgumentException) { normalized = text.ToLowerInvariant(); }
        var words = new List<string>();
        var start = -1;
        var index = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var inWord = Rune.IsLetter(rune) || Rune.IsNumber(rune) || start >= 0 && Rune.GetUnicodeCategory(rune)
                is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (inWord && start < 0) start = index;
            else if (!inWord && start >= 0)
            {
                words.Add(normalized[start..index]);
                start = -1;
            }
            index += rune.Utf16SequenceLength;
        }
        if (start >= 0) words.Add(normalized[start..]);
        return words;
    }
}

/// <summary>A stretch of time. <see cref="End"/> is not part of it.</summary>
/// <param name="Start">The first moment of the period.</param>
/// <param name="End">The moment the period ends.</param>
public readonly record struct ModuleHistoryPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Whether <paramref name="moment"/> lies in the period.</summary>
    public bool Contains(DateTimeOffset moment) => Start <= moment && moment < End;
}

/// <summary>The part of History a search looks at. An empty set does not limit it.</summary>
public sealed record ModuleHistorySearchScope
{
    /// <summary>When the records were made, or null for any time.</summary>
    public ModuleHistoryPeriod? Period { get; init; }

    /// <summary>Record sources such as <c>"dictation"</c> or <c>"file"</c>, as <see cref="ModuleHistoryRecord.Source"/> names them.</summary>
    public IReadOnlySet<string> Sources { get; init; } = new HashSet<string>();

    /// <summary>The process names of apps, as <see cref="ModuleHistoryApp.ProcessName"/> names them, compared without case.</summary>
    public IReadOnlySet<string> AppProcessNames { get; init; } = new HashSet<string>();

    /// <summary>
    /// Website domains such as <c>"example.com"</c>, compared as the index stores them: without case, surrounding
    /// spaces, a trailing dot or a leading <c>www.</c>.
    /// </summary>
    public IReadOnlySet<string> Domains { get; init; } = new HashSet<string>();

    /// <summary>The records the search is limited to, or null for no such limit. An empty set matches none.</summary>
    public IReadOnlySet<string>? RecordIds { get; init; }

    /// <summary>
    /// Whether a hit lies in the scope. A hit does not carry its app's process name, so a scope of apps includes none.
    /// </summary>
    public bool Includes(ModuleHistorySearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return (Period?.Contains(hit.RecordedAt) ?? true)
            && (Sources.Count == 0 || Sources.Contains(hit.Source))
            && AppProcessNames.Count == 0
            && (Domains.Count == 0 || hit.Domain is { } domain && Domains.Any(item => NormalizedDomain(item) is { } requested
                && string.Equals(requested, NormalizedDomain(domain), StringComparison.Ordinal)))
            && (RecordIds?.Contains(hit.RecordId) ?? true);
    }

    // A domain as the index stores it, or null when nothing is left.
    private static string? NormalizedDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal)) normalized = normalized[4..];
        return normalized.Length == 0 ? null : normalized;
    }
}

/// <summary>What a passage holds.</summary>
public enum ModuleHistoryPassageKind
{
    /// <summary>What was said.</summary>
    Transcript,
    /// <summary>A text made from the recording, such as an email, a translation or notes.</summary>
    Result
}

/// <summary>A place in a text, in UTF-16 code units. <see cref="End"/> is not part of it.</summary>
/// <param name="Start">The first code unit.</param>
/// <param name="End">The code unit after the last one.</param>
public readonly record struct ModuleTextSpan(int Start, int End)
{
    /// <summary>The number of code units.</summary>
    public int Length => End - Start;
}

/// <summary>A stretch of a recording that the index stores, finds and cites as one unit.</summary>
/// <param name="Kind">What the passage holds.</param>
/// <param name="Text">The passage's text.</param>
public sealed record ModuleHistoryPassage(ModuleHistoryPassageKind Kind, string Text)
{
    /// <summary>The speakers in the passage, in order of appearance. Empty without speaker detection.</summary>
    public IReadOnlyList<string> Speakers { get; init; } = [];

    /// <summary>Where the passage starts in the audio, in seconds, or null when unknown.</summary>
    public double? StartSeconds { get; init; }

    /// <summary>Where the passage ends in the audio, in seconds, or null when unknown.</summary>
    public double? EndSeconds { get; init; }

    /// <summary>
    /// The passage's place in its source text: the record's transcript or its result text. Null when unknown.
    /// </summary>
    public ModuleTextSpan? TextSpan { get; init; }

    /// <inheritdoc />
    public bool Equals(ModuleHistoryPassage? other) => other is not null && Kind == other.Kind && Text == other.Text
        && Speakers.SequenceEqual(other.Speakers, StringComparer.Ordinal) && StartSeconds == other.StartSeconds
        && EndSeconds == other.EndSeconds && TextSpan == other.TextSpan;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Text, Speakers.Count, StartSeconds, EndSeconds, TextSpan);
}

/// <summary>A passage a search found, with what a list of sources shows of its record.</summary>
/// <param name="RecordId">The record's identifier.</param>
/// <param name="RecordedAt">When the record was made.</param>
/// <param name="Source">Where the record came from, as <see cref="ModuleHistoryRecord.Source"/> names it.</param>
/// <param name="FirstOrdinal">The position of the passage in its record, counted from 0.</param>
/// <param name="LastOrdinal">The position of the last passage, when neighbours were joined; else <paramref name="FirstOrdinal"/>.</param>
/// <param name="Passage">The passage.</param>
public sealed record ModuleHistorySearchHit(
    string RecordId,
    DateTimeOffset RecordedAt,
    string Source,
    int FirstOrdinal,
    int LastOrdinal,
    ModuleHistoryPassage Passage)
{
    /// <summary>The record's title, such as the name of a recording from another device, or null.</summary>
    public string? Title { get; init; }

    /// <summary>The app a dictation was made in, or null.</summary>
    public string? AppName { get; init; }

    /// <summary>The website domain a dictation was made on, or null.</summary>
    public string? Domain { get; init; }

    /// <summary>Higher is better. Scores of different searches do not compare.</summary>
    public double Score { get; init; }
}

/// <summary>The newest records of a scope and how many records it holds.</summary>
/// <param name="Hits">The records' first passages, newest first.</param>
/// <param name="Total">How many records the scope holds.</param>
public sealed record ModuleHistoryNewestRecords(IReadOnlyList<ModuleHistorySearchHit> Hits, int Total);

/// <summary>A record's text as the index holds it.</summary>
/// <param name="Passages">What was said, in order.</param>
/// <param name="ContentHash">Changes with the record's content; <see cref="IHistorySearching.SetSummaryAsync"/> checks it.</param>
public sealed record ModuleHistoryTranscript(IReadOnlyList<ModuleHistoryPassage> Passages, string ContentHash);

/// <summary>A summary kept for a record and the provider that wrote it.</summary>
/// <param name="Text">The summary.</param>
/// <param name="ProviderId">The provider that wrote it.</param>
public sealed record ModuleHistorySummary(string Text, string ProviderId);

/// <summary>An app records were made in.</summary>
/// <param name="Name">The app's display name.</param>
/// <param name="ProcessName">The app's process name in lowercase, such as <c>"winword"</c>.</param>
public sealed record ModuleHistoryApp(string Name, string ProcessName);

/// <summary>The names in the index that a search can refer to.</summary>
/// <param name="Speakers">Speaker names, sorted.</param>
/// <param name="Apps">Apps records were made in.</param>
/// <param name="Domains">Website domains records were made on, sorted.</param>
public sealed record ModuleHistoryVocabulary(IReadOnlyList<string> Speakers, IReadOnlyList<ModuleHistoryApp> Apps, IReadOnlyList<string> Domains)
{
    /// <summary>A vocabulary without names.</summary>
    public static ModuleHistoryVocabulary Empty { get; } = new([], [], []);
}
