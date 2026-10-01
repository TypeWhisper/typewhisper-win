using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>The smart mailboxes of the History window, as on macOS.</summary>
public enum HistoryMailbox
{
    /// <summary>Entries still open in the Inbox.</summary>
    Inbox,
    /// <summary>Every saved entry.</summary>
    All,
    /// <summary>Entries with saved audio.</summary>
    WithAudio,
    /// <summary>Entries whose processing failed.</summary>
    Failed
}

/// <summary>History list ordering.</summary>
public enum HistorySort
{
    /// <summary>Most recent first.</summary>
    NewestFirst,
    /// <summary>Oldest first.</summary>
    OldestFirst,
    /// <summary>Longest recording first.</summary>
    Duration,
    /// <summary>Alphabetical by app, then newest first.</summary>
    AppName
}

/// <summary>History date filter.</summary>
public enum HistoryDateRange
{
    /// <summary>No date limit.</summary>
    AllTime,
    /// <summary>Today and the preceding six days.</summary>
    Last7Days,
    /// <summary>Today and the preceding 29 days.</summary>
    Last30Days,
    /// <summary>Today and the preceding 89 days.</summary>
    Last90Days
}

/// <summary>Collapsible date sections of the History list.</summary>
public enum HistoryDateGroup
{
    /// <summary>The current local day.</summary>
    Today,
    /// <summary>The previous local day.</summary>
    Yesterday,
    /// <summary>Earlier this week (weeks start on Monday).</summary>
    ThisWeek,
    /// <summary>Earlier this month.</summary>
    ThisMonth,
    /// <summary>Everything before this month.</summary>
    Older
}

/// <summary>What the History list shows: a smart mailbox, or the entries of one device, optionally from one source.</summary>
/// <param name="Mailbox">The selected mailbox, or null when a device is selected.</param>
/// <param name="Source">A <see cref="HistoryWorkspace.SourceOf"/> value of this PC, used when no mailbox is selected.</param>
/// <param name="Device">Another device's History origin identity; null means this PC.</param>
public sealed record HistoryScope(HistoryMailbox? Mailbox, string? Source = null, string? Device = null)
{
    /// <summary>The default selection when the window opens, as on macOS.</summary>
    public static HistoryScope All { get; } = new(HistoryMailbox.All);
}

/// <summary>A word of a text comparison.</summary>
/// <param name="Text">The word, including its trailing whitespace.</param>
/// <param name="Kind">Whether the word was kept, removed or added.</param>
public sealed record HistoryDiffToken(string Text, HistoryDiffKind Kind);

/// <summary>The role of a word in a comparison.</summary>
public enum HistoryDiffKind
{
    /// <summary>Present in both texts.</summary>
    Unchanged,
    /// <summary>Only in the original text.</summary>
    Removed,
    /// <summary>Only in the final text.</summary>
    Added
}

/// <summary>Filtering, grouping and comparison for the History window. Pure functions over saved records.</summary>
public static class HistoryWorkspace
{
    /// <summary>Persisted Inbox state of an entry that still needs attention.</summary>
    public const string InboxOpen = "open";
    /// <summary>Persisted Inbox state of an entry marked complete.</summary>
    public const string InboxCompleted = "completed";
    /// <summary>Texts longer than this many words are not compared, as the comparison is quadratic.</summary>
    public const int MaximumDiffWords = 4000;

    /// <summary>The local source an entry came from: dictation, recording, file or other.</summary>
    public static string SourceOf(TranscriptionRecord record) => record.SourceKind switch
    {
        "dictation" or null => "dictation",
        "recording" => "recording",
        "file" => "file",
        _ => "other"
    };

    /// <summary>Returns whether the entry belongs to the mailbox.</summary>
    public static bool InMailbox(TranscriptionRecord record, HistoryMailbox mailbox) => mailbox switch
    {
        HistoryMailbox.Inbox => record.InboxState == InboxOpen,
        HistoryMailbox.WithAudio => !string.IsNullOrWhiteSpace(record.AudioFileName),
        HistoryMailbox.Failed => record.Status != TranscriptionRecordStatus.Succeeded || record.ProcessingState == "failed",
        _ => true
    };

    /// <summary>Returns whether the entry was created on this PC.</summary>
    public static bool IsLocal(TranscriptionRecord record, string? localDeviceId) =>
        string.IsNullOrWhiteSpace(record.OriginDeviceId) || record.OriginDeviceId == localDeviceId;

    /// <summary>Returns whether the entry belongs to the scope.</summary>
    public static bool InScope(TranscriptionRecord record, HistoryScope scope, string? localDeviceId = null) =>
        scope.Mailbox is { } mailbox ? InMailbox(record, mailbox)
        : scope.Device is { } device ? !IsLocal(record, localDeviceId) && record.OriginDeviceId == device
        : IsLocal(record, localDeviceId) && (scope.Source is null || SourceOf(record) == scope.Source);

    /// <summary>Returns whether the entry was post-processed, so its original text differs from the final text.</summary>
    public static bool WasProcessed(TranscriptionRecord record) =>
        !string.IsNullOrWhiteSpace(record.FinalText) && !string.Equals(record.RawText.Trim(), record.FinalText.Trim(), StringComparison.Ordinal);

    /// <summary>Applies scope, search, date range and app filters, then sorts.</summary>
    public static IReadOnlyList<TranscriptionRecord> Query(IEnumerable<TranscriptionRecord> records, HistoryScope scope, string? search,
        HistoryDateRange range, string? app, HistorySort sort, DateTimeOffset now, TimeZoneInfo? zone = null, string? localDeviceId = null)
    {
        zone ??= TimeZoneInfo.Local;
        var term = search?.Trim() ?? "";
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var from = range switch
        {
            HistoryDateRange.Last7Days => today.AddDays(-6),
            HistoryDateRange.Last30Days => today.AddDays(-29),
            HistoryDateRange.Last90Days => today.AddDays(-89),
            _ => DateOnly.MinValue
        };
        var filtered = records.Where(record => InScope(record, scope, localDeviceId)
            && (from == DateOnly.MinValue || LocalDate(record.Timestamp, zone) >= from)
            && (string.IsNullOrWhiteSpace(app) || string.Equals(AppOf(record), app, StringComparison.OrdinalIgnoreCase))
            && (term.Length == 0 || record.FinalText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || record.RawText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || record.AppName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
        return (sort switch
        {
            HistorySort.OldestFirst => filtered.OrderBy(record => record.Timestamp),
            HistorySort.Duration => filtered.OrderByDescending(record => double.IsFinite(record.DurationSeconds) ? record.DurationSeconds : 0)
                .ThenByDescending(record => record.Timestamp),
            HistorySort.AppName => filtered.OrderBy(record => AppOf(record) ?? "￿", StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(record => record.Timestamp),
            _ => filtered.OrderByDescending(record => record.Timestamp)
        }).ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>A readable label for the provider and model that produced an entry, without assuming the current model.</summary>
    public static string ModelLabel(string? engine, string? model)
    {
        var name = model?.Trim() switch
        {
            null or "" => null,
            "parakeet-tdt-0.6b" => "Parakeet TDT 0.6B",
            "parakeet-ultra-0.6b" => "Parakeet Ultra 0.6B",
            "canary-180m-flash" => "Canary 180M Flash",
            "whisper-large-v3" => "Whisper Large V3",
            "whisper-large-v3-turbo" => "Whisper Large V3 Turbo",
            var other => other
        };
        if (name is null) return Loc.T("Model not recorded");
        var provider = engine?.Trim() switch
        {
            "groq" => "Groq",
            "sherpa-onnx" => Loc.T("Local"),
            null or "" => null,
            var other => other
        };
        return provider is null ? name : $"{provider} · {name}";
    }

    /// <summary>The app name shown for an entry, falling back to its process name.</summary>
    public static string? AppOf(TranscriptionRecord record) =>
        string.IsNullOrWhiteSpace(record.AppName) ? string.IsNullOrWhiteSpace(record.AppProcessName) ? null : record.AppProcessName : record.AppName;

    /// <summary>The distinct apps of the given entries, for the app filter.</summary>
    public static IReadOnlyList<string> Apps(IEnumerable<TranscriptionRecord> records) => records.Select(AppOf).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The date section of an entry relative to <paramref name="now"/>.</summary>
    public static HistoryDateGroup GroupOf(DateTime timestampUtc, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var date = LocalDate(timestampUtc, zone);
        if (date >= today) return HistoryDateGroup.Today;
        if (date == today.AddDays(-1)) return HistoryDateGroup.Yesterday;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (date >= weekStart) return HistoryDateGroup.ThisWeek;
        if (date.Year == today.Year && date.Month == today.Month) return HistoryDateGroup.ThisMonth;
        return HistoryDateGroup.Older;
    }

    /// <summary>
    /// Compares two texts word by word. Returns null when either text is longer than <see cref="MaximumDiffWords"/>.
    /// </summary>
    public static IReadOnlyList<HistoryDiffToken>? WordDiff(string original, string final)
    {
        var a = Words(original);
        var b = Words(final);
        if (a.Length > MaximumDiffWords || b.Length > MaximumDiffWords) return null;
        // Longest common subsequence over words, compared without surrounding whitespace.
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = Same(a[i], b[j]) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var tokens = new List<HistoryDiffToken>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (Same(a[x], b[y])) { tokens.Add(new(b[y], HistoryDiffKind.Unchanged)); x++; y++; }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) tokens.Add(new(a[x++], HistoryDiffKind.Removed));
            else tokens.Add(new(b[y++], HistoryDiffKind.Added));
        }
        while (x < a.Length) tokens.Add(new(a[x++], HistoryDiffKind.Removed));
        while (y < b.Length) tokens.Add(new(b[y++], HistoryDiffKind.Added));
        return tokens;

        static bool Same(string left, string right) => string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal);
    }

    private static string[] Words(string text)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]) || i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) continue;
            words.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) words.Add(text[start..]);
        return words.Where(word => word.Trim().Length > 0).ToArray();
    }

    private static DateOnly LocalDate(DateTime timestampUtc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc), zone));
}
