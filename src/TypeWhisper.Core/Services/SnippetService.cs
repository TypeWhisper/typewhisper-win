using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides snippet service behavior.
/// </summary>
public sealed partial class SnippetService : ISnippetService
{
    private readonly string _filePath;
    private List<Snippet> _cache = [];
    private bool _cacheLoaded;
    private string? _fileBaseline;

    /// <summary>
    /// Gets the configured snippets in display order.
    /// </summary>
    public IReadOnlyList<Snippet> Snippets
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    /// <summary>
    /// Gets the all tags.
    /// </summary>
    public IReadOnlyList<string> AllTags
    {
        get
        {
            EnsureCacheLoaded();
            return _cache
                .SelectMany(s => s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Raised when snippets changes.
    /// </summary>
    public event Action? SnippetsChanged;

    /// <summary>
    /// Initializes a new instance of the SnippetService class.
    /// </summary>
    public SnippetService(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Gets why the snippet file could not be read or parsed, or null when it loaded or does not exist yet.
    /// </summary>
    /// <remarks>
    /// While set, <see cref="Snippets"/> is empty and every mutation is refused, so a locked or corrupt file is
    /// never replaced by that empty list. <see cref="Reload"/> clears it once the file can be read again.
    /// </remarks>
    public Exception? LoadError { get; private set; }

    /// <summary>
    /// Gets why the most recent mutation was not written to disk, or null when it was persisted.
    /// </summary>
    public Exception? LastSaveError { get; private set; }

    /// <summary>
    /// Reads the snippet file again, for example after a sync client released it, and reports whether it loaded.
    /// </summary>
    public bool Reload()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        _cacheLoaded = false;
        EnsureCacheLoaded();
        if (LoadError is not null) return false;

        SnippetsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Adds snippet.
    /// </summary>
    public void AddSnippet(Snippet snippet)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.Add(BackfillTimestamps(snippet));
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Updates snippet.
    /// </summary>
    public void UpdateSnippet(Snippet snippet)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var idx = _cache.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = BackfillTimestamps(snippet) with { UpdatedAt = NextUpdatedAt(existing.UpdatedAt) };
        }
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Deletes snippet.
    /// </summary>
    public void DeleteSnippet(string id)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.RemoveAll(s => s.Id == id);
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Applies snippets.
    /// </summary>
    public string ApplySnippets(string text, Func<string>? clipboardProvider = null)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        return ApplySnippetsSnapshot(text, _cache.ToArray(), clipboardProvider, IncrementUsageCount);
    }

    /// <summary>Expands a recording's fixed snippet catalog without reading or writing storage.</summary>
    public static string ApplySnippetsSnapshot(string text, IReadOnlyList<Snippet> snippets,
        Func<string>? clipboardProvider = null, Action<string>? onApplied = null)
    {
        var activeSnippets = snippets
            .Where(s => s.IsEnabled && !string.IsNullOrEmpty(s.Trigger))
            .OrderByDescending(s => s.Trigger.Length)
            .ToArray();
        if (activeSnippets.Length == 0) return text;

        // A literal trigger must not split a combining sequence or an emoji grapheme.
        var textElementStarts = StringInfo.ParseCombiningCharacters(text);
        bool IsTextElementBoundary(int index) => index == text.Length || Array.BinarySearch(textElementStarts, index) >= 0;
        var replacements = new List<(int Start, int End, string Text)>();
        bool[]? occupied = null;

        foreach (var snippet in activeSnippets)
        {
            var comparison = snippet.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var requiresLeftBoundary = !IsScriptWithoutWhitespaceBoundaries(snippet.Trigger.EnumerateRunes().First());
            var requiresRightBoundary = !IsScriptWithoutWhitespaceBoundaries(snippet.Trigger.EnumerateRunes().Last());
            string? expanded = null;
            var searchFrom = 0;

            while (searchFrom <= text.Length - snippet.Trigger.Length)
            {
                var index = text.IndexOf(snippet.Trigger, searchFrom, comparison);
                if (index < 0) break;

                var end = index + snippet.Trigger.Length;
                searchFrom = index + 1;
                if (!IsTextElementBoundary(index) || !IsTextElementBoundary(end))
                    continue;
                if ((requiresLeftBoundary && IsWordContinuation(text, index - 1, -1)) ||
                    (requiresRightBoundary && IsWordContinuation(text, end, 1)))
                    continue;
                if (occupied is not null && occupied.AsSpan(index, snippet.Trigger.Length).Contains(true))
                    continue;

                expanded ??= ExpandPlaceholders(snippet.Replacement, clipboardProvider);
                occupied ??= new bool[text.Length];
                if (end < text.Length && !occupied[end] && text[end] is '.' or '!' or '?' && IsTextElementBoundary(end + 1))
                    end++;
                occupied.AsSpan(index, end - index).Fill(true);
                replacements.Add((index, end, expanded));
                searchFrom = end;
            }

            if (expanded is not null)
                onApplied?.Invoke(snippet.Id);
        }

        if (replacements.Count == 0) return text;

        // Resolve all matches against the original transcript, keeping longest-trigger priority.
        var result = new StringBuilder();
        var copiedThrough = 0;
        foreach (var replacement in replacements.OrderBy(r => r.Start))
        {
            result.Append(text, copiedThrough, replacement.Start - copiedThrough).Append(replacement.Text);
            copiedThrough = replacement.End;
        }
        return result.Append(text, copiedThrough, text.Length - copiedThrough).ToString();
    }

    private static bool IsWordContinuation(string text, int index, int direction, bool includeWordPunctuation = true)
    {
        while (index >= 0 && index < text.Length)
        {
            // Decode the preceding scalar from its low surrogate when checking a left boundary.
            if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1]))
                index--;
            if (!Rune.TryGetRuneAt(text, index, out var rune)) return false;

            // Zero-width space separates words; other format controls do not create boundaries.
            if (rune.Value == 0x200B) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format)
            {
                index += direction > 0 ? rune.Utf16SequenceLength : -1;
                continue;
            }

            // Unspaced scripts start a new word next to Latin text, as in "我的email是" (UAX #29).
            if (IsScriptWithoutWhitespaceBoundaries(rune)) return false;

            // Internal apostrophes and Hebrew gershayim join words; surrounding quotes remain separators.
            if (includeWordPunctuation && rune.Value is '\'' or '\u2018' or '\u2019' or '\u05F4')
                return IsWordContinuation(text, index - 1, -1, false) && IsWordContinuation(text, index + 1, 1, false);

            return rune.Value == 0x05F3 || // Hebrew geresh is also word-internal at an abbreviation's end.
                Rune.IsLetter(rune) || Rune.IsNumber(rune) || category is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
        }
        return false;
    }

    // Preserve unspaced scripts independently at each trigger edge; digit sequences still need boundaries.
    // Blocks and South East Asian (SA) scripts: https://www.unicode.org/reports/tr14/#SA
    private static bool IsScriptWithoutWhitespaceBoundaries(Rune rune) =>
        (Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) && rune.Value is
            >= 0x0E00 and <= 0x0EFF // Thai and Lao
            or >= 0x1000 and <= 0x109F // Myanmar
            or >= 0x1100 and <= 0x11FF // Hangul Jamo
            or >= 0x1780 and <= 0x17FF // Khmer
            or >= 0x1950 and <= 0x19DF // Tai Le and New Tai Lue
            or >= 0x1A20 and <= 0x1AAF // Tai Tham
            or >= 0x3040 and <= 0x30FF // Hiragana and Katakana
            or >= 0x3130 and <= 0x318F // Hangul Compatibility Jamo
            or >= 0x31F0 and <= 0x31FF // Katakana Phonetic Extensions
            or >= 0x3400 and <= 0x4DBF // CJK Extension A
            or >= 0x4E00 and <= 0x9FFF // CJK ideographs
            or >= 0xA960 and <= 0xA97F // Hangul Jamo Extended-A
            or >= 0xA9E0 and <= 0xA9FF // Myanmar Extended-B
            or >= 0xAA60 and <= 0xAADF // Myanmar Extended-A and Tai Viet
            or >= 0xAC00 and <= 0xD7FF // Hangul syllables and Jamo Extended-B
            or >= 0xF900 and <= 0xFAFF // CJK Compatibility Ideographs
            or >= 0xFF66 and <= 0xFF9F // Halfwidth Katakana
            or >= 0x11700 and <= 0x1174F // Ahom
            or >= 0x1AFF0 and <= 0x1B16F // Supplementary Kana blocks
            or >= 0x20000 and <= 0x2A6DF // CJK Extension B
            or >= 0x2A700 and <= 0x2EE5F // CJK Extensions C-F and I
            or >= 0x2F800 and <= 0x2FA1F // CJK Compatibility Ideographs Supplement
            or >= 0x30000 and <= 0x3347F; // CJK Extensions G, H and J

    /// <summary>
    /// Exports the current data as JSON.
    /// </summary>
    public string ExportToJson()
    {
        EnsureCacheLoaded();
        return JsonSerializer.Serialize(_cache, SnippetJsonContext.Default.ListSnippet);
    }

    /// <summary>
    /// Imports from json.
    /// </summary>
    public int ImportFromJson(string json)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        var imported = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet);
        if (imported is null or { Count: 0 }) return 0;

        if (!TryBeginMutation(out var rollback)) return 0;
        var existingTriggers = _cache.Select(s => s.Trigger).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var snippet in imported)
        {
            if (existingTriggers.Contains(snippet.Trigger)) continue;

            var newSnippet = BackfillTimestamps(snippet with { Id = Guid.NewGuid().ToString() });
            _cache.Add(newSnippet);
            existingTriggers.Add(newSnippet.Trigger);
            count++;
        }

        return count > 0 && TryCommitMutation(rollback) ? count : 0;
    }

    private static string ExpandPlaceholders(string template, Func<string>? clipboardProvider)
    {
        var now = DateTime.Now;

        template = template
            .Replace("{day}", now.ToString("dddd"))
            .Replace("{year}", now.Year.ToString());

        template = PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var format = match.Groups[2].Success ? match.Groups[2].Value : null;

            return name switch
            {
                "date" => now.ToString(format ?? "yyyy-MM-dd"),
                "time" => now.ToString(format ?? "HH:mm"),
                "datetime" => now.ToString(format ?? "yyyy-MM-dd HH:mm"),
                "clipboard" => clipboardProvider?.Invoke() ?? "",
                _ => match.Value
            };
        });

        return template;
    }

    [GeneratedRegex(@"\{(date|time|datetime|clipboard)(?::([^}]+))?\}")]
    private static partial Regex PlaceholderRegex();

    private void IncrementUsageCount(string id)
    {
        var idx = _cache.FindIndex(s => s.Id == id);
        if (idx >= 0)
        {
            _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
            // Usage counters are best effort: a failed write is recorded and retried with the next save.
            _ = SaveToDisk(_cache);
        }
    }

    /// <summary>
    /// Returns user data sync snippets.
    /// </summary>
    public IReadOnlyList<UserDataSyncSnippet> GetUserDataSyncSnippets()
    {
        EnsureCacheLoaded();
        return _cache
            .Select(snippet => new UserDataSyncSnippet(
                snippet.Trigger,
                snippet.Replacement,
                snippet.CaseSensitive,
                snippet.IsEnabled,
                snippet.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                snippet.CreatedAt,
                snippet.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// Applies user data sync mutations.
    /// </summary>
    public void ApplyUserDataSyncMutations(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        using var profileMutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;

        var changed = false;
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case UserDataSyncMutation.UpsertSnippet upsert:
                    changed |= UpsertSyncedSnippet(upsert.Snippet);
                    break;
                case UserDataSyncMutation.DeleteSnippet delete:
                    changed |= DeleteSyncedSnippet(delete.ItemId);
                    break;
            }
        }

        if (!changed)
            return;

        TryCommitMutation(rollback);
    }

    private bool UpsertSyncedSnippet(UserDataSyncSnippet synced)
    {
        var targetId = UserDataSyncIdentity.SnippetItemId(synced.Trigger);
        var idx = _cache.FindIndex(snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == targetId);
        var tags = string.Join(",", synced.Tags);

        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = existing with
            {
                Trigger = synced.Trigger,
                Replacement = synced.Replacement,
                CaseSensitive = synced.CaseSensitive,
                IsEnabled = synced.IsEnabled,
                Tags = tags,
                UpdatedAt = synced.UpdatedAt
            };
            return true;
        }

        _cache.Add(new Snippet
        {
            Id = Guid.NewGuid().ToString(),
            Trigger = synced.Trigger,
            Replacement = synced.Replacement,
            CaseSensitive = synced.CaseSensitive,
            IsEnabled = synced.IsEnabled,
            Tags = tags,
            CreatedAt = synced.CreatedAt,
            UpdatedAt = synced.UpdatedAt
        });
        return true;
    }

    private bool DeleteSyncedSnippet(string itemId)
    {
        var removed = _cache.RemoveAll(snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == itemId);
        return removed > 0;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        LoadError = null;
        try
        {
            var json = ReviewedCatalogTransaction.Read(_filePath);
            _fileBaseline = json;
            // A zero-length file holds nothing to protect, so it counts as an empty catalog like a missing one.
            _cache = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<Snippet>>(json) ?? [];
            _cache = _cache.Select(BackfillTimestamps).ToList();
        }
        catch (FileNotFoundException) { _cache = []; }
        catch (DirectoryNotFoundException) { _cache = []; }
        catch (Exception ex)
        {
            // The file exists but cannot be trusted; keep it off limits instead of treating it as empty.
            LoadError = ex;
            _cache = [];
        }

        _cacheLoaded = true;
    }

    // Mutations are refused rather than thrown while the file is unreadable: dictation and sync reach them, and an
    // exception there would abort the dictation or the sync cycle. The cache is empty in that state, so writing it
    // would replace the user's snippets with an almost empty list. Callers can inspect LoadError and LastSaveError.
    private bool TryBeginMutation(out List<Snippet> rollback)
    {
        EnsureCacheLoaded();
        if (LoadError is null)
        {
            rollback = _cache.ToList();
            return true;
        }

        LastSaveError = new InvalidOperationException(
            "The snippet file could not be loaded, so changes are not saved until it loads again.", LoadError);
        rollback = [];
        return false;
    }

    // A failed write restores the previous cache and raises no event. A stale catalog needs an explicit reload.
    private bool TryCommitMutation(List<Snippet> rollback)
    {
        if (SaveToDisk(_cache))
        {
            SnippetsChanged?.Invoke();
            return true;
        }

        _cache = rollback;
        return false;
    }

    /// <inheritdoc />
    public bool TryReplaceAll(IReadOnlyList<Snippet> snippets)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out _)) return false;
        var replacement = snippets.Select(BackfillTimestamps).ToList();
        if (!SaveToDisk(replacement))
            return false;

        _cache = replacement;
        SnippetsChanged?.Invoke();
        return true;
    }

    private bool SaveToDisk(IReadOnlyList<Snippet> snippets)
    {
        var json = JsonSerializer.Serialize(snippets, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            ReviewedCatalogTransaction.Commit(_filePath, _fileBaseline, json);
            _fileBaseline = json;
        }
        catch (Exception error)
        {
            LastSaveError = error;
            return false;
        }

        LastSaveError = null;
        return true;
    }

    private static Snippet BackfillTimestamps(Snippet snippet)
    {
        var createdAt = snippet.CreatedAt == default ? DateTime.UtcNow : NormalizeUtc(snippet.CreatedAt);
        var updatedAt = snippet.UpdatedAt == default ? createdAt : NormalizeUtc(snippet.UpdatedAt);
        return snippet with { CreatedAt = createdAt, UpdatedAt = updatedAt };
    }

    private static DateTime NextUpdatedAt(DateTime previousUpdatedAt)
    {
        var previous = previousUpdatedAt == default ? DateTime.UtcNow : NormalizeUtc(previousUpdatedAt);
        var now = DateTime.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }

    private static DateTime NormalizeUtc(DateTime date) =>
        date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
}

[JsonSerializable(typeof(List<Snippet>))]
internal partial class SnippetJsonContext : JsonSerializerContext;
