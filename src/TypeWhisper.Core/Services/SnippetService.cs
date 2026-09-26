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
    /// Adds snippet.
    /// </summary>
    public void AddSnippet(Snippet snippet)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        _cache.Add(BackfillTimestamps(snippet));
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Updates snippet.
    /// </summary>
    public void UpdateSnippet(Snippet snippet)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = BackfillTimestamps(snippet) with { UpdatedAt = NextUpdatedAt(existing.UpdatedAt) };
        }
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Deletes snippet.
    /// </summary>
    public void DeleteSnippet(string id)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        _cache.RemoveAll(s => s.Id == id);
        SaveToDisk();
        SnippetsChanged?.Invoke();
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
            .OrderByDescending(s => s.Trigger.Length);
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
                if ((requiresLeftBoundary && IsWordContinuation(text, index - 1)) ||
                    (requiresRightBoundary && IsWordContinuation(text, end)))
                    continue;
                if (occupied is not null && occupied.AsSpan(index, snippet.Trigger.Length).Contains(true))
                    continue;

                expanded ??= ExpandPlaceholders(snippet.Replacement, clipboardProvider);
                occupied ??= new bool[text.Length];
                if (end < text.Length && !occupied[end] && text[end] is '.' or '!' or '?')
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

    private static bool IsWordContinuation(string text, int index, bool includeApostrophes = true)
    {
        if (index < 0 || index >= text.Length) return false;
        // Decode the preceding scalar from its low surrogate when checking a left boundary.
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1]))
            index--;
        if (!Rune.TryGetRuneAt(text, index, out var rune)) return false;

        // Internal apostrophes join contractions; surrounding quotation marks remain separators.
        if (includeApostrophes && rune.Value is '\'' or '\u2018' or '\u2019')
            return IsWordContinuation(text, index - 1, false) && IsWordContinuation(text, index + 1, false);

        return rune.Value is 0x200C or 0x200D || // ZWNJ and ZWJ can occur inside orthographic words.
            Rune.IsLetter(rune) || Rune.IsNumber(rune) || Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
    }

    // Preserve unspaced scripts independently at each trigger edge; digit sequences still need boundaries.
    // Blocks and South East Asian (SA) scripts: https://www.unicode.org/reports/tr14/#SA
    private static bool IsScriptWithoutWhitespaceBoundaries(Rune rune) =>
        !Rune.IsNumber(rune) && rune.Value is
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

        EnsureCacheLoaded();
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

        if (count > 0)
        {
            SaveToDisk();
            SnippetsChanged?.Invoke();
        }

        return count;
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
            SaveToDisk();
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
        EnsureCacheLoaded();

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

        SaveToDisk();
        SnippetsChanged?.Invoke();
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

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<Snippet>>(json) ?? [];
                _cache = _cache.Select(BackfillTimestamps).ToList();
            }
        }
        catch
        {
            _cache = [];
        }

        _cacheLoaded = true;
    }

    private void SaveToDisk()
    {
        _ = SaveToDisk(_cache);
    }

    /// <inheritdoc />
    public bool TryReplaceAll(IReadOnlyList<Snippet> snippets)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        var replacement = snippets.Select(BackfillTimestamps).ToList();
        if (!SaveToDisk(replacement))
            return false;

        _cache = replacement;
        SnippetsChanged?.Invoke();
        return true;
    }

    private bool SaveToDisk(IReadOnlyList<Snippet> snippets) =>
        AtomicFileWriter.TryWriteAllText(
            _filePath,
            JsonSerializer.Serialize(snippets, new JsonSerializerOptions { WriteIndented = true }));

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
