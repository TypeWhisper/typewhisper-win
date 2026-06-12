using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides dictionary service behavior.
/// </summary>
public sealed class DictionaryService : IDictionaryService
{
    private const string PackEntryPrefix = "pack:";

    private readonly string _filePath;
    private List<DictionaryEntry> _cache = [];
    private bool _cacheLoaded;

    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> Entries
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    /// <summary>
    /// Raised when entries changes.
    /// </summary>
    public event Action? EntriesChanged;

    /// <summary>
    /// Initializes a new instance of the DictionaryService class.
    /// </summary>
    public DictionaryService(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Adds a dictionary entry and persists the updated dictionary.
    /// </summary>
    public void AddEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        _cache.Add(BackfillTimestamps(entry));
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Adds entries.
    /// </summary>
    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        EnsureCacheLoaded();
        _cache.AddRange(entries.Select(BackfillTimestamps));
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Updates entry.
    /// </summary>
    public void UpdateEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = BackfillTimestamps(entry) with { UpdatedAt = NextUpdatedAt(existing.UpdatedAt) };
        }
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Deletes entry.
    /// </summary>
    public void DeleteEntry(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(e => e.Id == id);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Deletes entries.
    /// </summary>
    public void DeleteEntries(IEnumerable<string> ids)
    {
        EnsureCacheLoaded();
        var idSet = ids.ToHashSet();
        _cache.RemoveAll(e => idSet.Contains(e.Id));
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Applies corrections.
    /// </summary>
    public string ApplyCorrections(string text)
    {
        EnsureCacheLoaded();
        var corrections = _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction && e.Replacement is not null)
            .OrderByDescending(e => e.Original.Length);

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (text.Contains(entry.Original, comparison))
            {
                var pattern = BuildCorrectionPattern(entry.Original);
                var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                text = Regex.Replace(text, pattern, entry.Replacement!, options);

                IncrementUsageCount(entry.Id);
            }
        }

        return text;
    }

    /// <summary>
    /// Returns terms for prompt.
    /// </summary>
    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = GetEnabledTerms();

        if (terms.Count == 0) return null;
        return string.Join(", ", terms);
    }

    /// <summary>
    /// Returns enabled terms.
    /// </summary>
    public IReadOnlyList<string> GetEnabledTerms()
    {
        EnsureCacheLoaded();
        return NormalizeTerms(_cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original));
    }

    /// <summary>
    /// Returns enabled corrections.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> GetEnabledCorrections()
    {
        EnsureCacheLoaded();
        return _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction)
            .OrderBy(e => e.Original, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sets terms.
    /// </summary>
    public void SetTerms(IEnumerable<string> terms, bool replaceExisting)
    {
        EnsureCacheLoaded();

        var normalized = NormalizeTerms(terms);
        var desiredByKey = normalized.ToDictionary(TermKey, term => term);
        var existingTerms = _cache.Where(e => e.EntryType == DictionaryEntryType.Term).ToList();
        var touchedTerms = new List<DictionaryEntry>();
        var untouchedTerms = new List<DictionaryEntry>();

        foreach (var entry in existingTerms)
        {
            var key = TermKey(entry.Original);
            if (desiredByKey.TryGetValue(key, out var desiredTerm))
            {
                var idx = _cache.FindIndex(e => e.Id == entry.Id);
                if (idx >= 0)
                {
                    var updated = entry with
                    {
                        Original = desiredTerm,
                        IsEnabled = true,
                        UpdatedAt = NextUpdatedAt(entry.UpdatedAt)
                    };
                    _cache[idx] = updated;
                    touchedTerms.Add(updated);
                }
            }
            else if (replaceExisting)
            {
                _cache.Remove(entry);
            }
            else
            {
                untouchedTerms.Add(entry);
            }
        }

        var existingKeys = existingTerms.Select(e => TermKey(e.Original)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var addedTerms = normalized
            .Where(term => !existingKeys.Contains(TermKey(term)))
            .Select(term => new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Term,
                Original = term,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();
        _cache.AddRange(addedTerms);

        if (replaceExisting)
        {
            ReorderTerms(
                normalized
                    .Select(term => _cache.First(e =>
                        e.EntryType == DictionaryEntryType.Term &&
                        TermKey(e.Original) == TermKey(term)))
                    .ToList());
        }
        else if (touchedTerms.Count > 0)
        {
            ReorderTerms([.. touchedTerms, .. addedTerms, .. untouchedTerms]);
        }

        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Removes all terms.
    /// </summary>
    public void RemoveAllTerms()
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(e => e.EntryType == DictionaryEntryType.Term);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Deletes term.
    /// </summary>
    public bool DeleteTerm(string term)
    {
        EnsureCacheLoaded();
        var key = TermKey(term);
        var removed = _cache.RemoveAll(e =>
            e.EntryType == DictionaryEntryType.Term &&
            TermKey(e.Original) == key);

        if (removed == 0)
            return false;

        SaveToDisk();
        EntriesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Upserts correction.
    /// </summary>
    public void UpsertCorrection(string original, string replacement, bool caseSensitive)
    {
        EnsureCacheLoaded();
        var existing = _cache.FirstOrDefault(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var idx = _cache.FindIndex(e => e.Id == existing.Id);
            if (idx >= 0)
            {
                _cache[idx] = existing with
                {
                    Original = original,
                    Replacement = replacement,
                    CaseSensitive = caseSensitive,
                    IsEnabled = true,
                    UpdatedAt = NextUpdatedAt(existing.UpdatedAt)
                };
            }
        }
        else
        {
            var now = DateTime.UtcNow;
            _cache.Add(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement,
                CaseSensitive = caseSensitive,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Deletes correction.
    /// </summary>
    public bool DeleteCorrection(string original)
    {
        EnsureCacheLoaded();
        var removed = _cache.RemoveAll(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
            return false;

        SaveToDisk();
        EntriesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Learns correction.
    /// </summary>
    public void LearnCorrection(string original, string replacement)
    {
        EnsureCacheLoaded();

        var existing = _cache.FirstOrDefault(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            UpdateEntry(existing with { Replacement = replacement, UsageCount = existing.UsageCount + 1 });
        }
        else
        {
            var now = DateTime.UtcNow;
            AddEntry(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    /// <summary>
    /// Activates pack.
    /// </summary>
    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var existingOriginals = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newEntries = pack.Terms
            .Where(t => !existingOriginals.Contains(t))
            .Select(t =>
            {
                var now = DateTime.UtcNow;
                return new DictionaryEntry
                {
                    Id = $"{PackEntryPrefix}{pack.Id}:{t}",
                    EntryType = DictionaryEntryType.Term,
                    Original = t,
                    CreatedAt = now,
                    UpdatedAt = now
                };
            })
            .ToList();

        if (newEntries.Count > 0)
        {
            _cache.AddRange(newEntries);
            SaveToDisk();
            EntriesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Deactivates pack.
    /// </summary>
    public void DeactivatePack(string packId)
    {
        EnsureCacheLoaded();

        var prefix = $"pack:{packId}:";
        var removed = _cache.RemoveAll(e => e.Id.StartsWith(prefix, StringComparison.Ordinal));

        if (removed > 0)
        {
            SaveToDisk();
            EntriesChanged?.Invoke();
        }
    }

    private void IncrementUsageCount(string id)
    {
        var idx = _cache.FindIndex(e => e.Id == id);
        if (idx >= 0)
        {
            _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
            SaveToDisk();
        }
    }

    /// <summary>
    /// Returns user data sync entries.
    /// </summary>
    public IReadOnlyList<UserDataSyncDictionaryEntry> GetUserDataSyncEntries()
    {
        EnsureCacheLoaded();
        return _cache
            .Where(IsUserAuthored)
            .Select(entry => new UserDataSyncDictionaryEntry(
                entry.EntryType == DictionaryEntryType.Term
                    ? UserDataSyncDictionaryEntryType.Term
                    : UserDataSyncDictionaryEntryType.Correction,
                entry.Original,
                entry.EntryType == DictionaryEntryType.Correction ? entry.Replacement ?? string.Empty : null,
                entry.CaseSensitive,
                entry.IsEnabled,
                entry.CreatedAt,
                entry.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// Applies user data sync mutations.
    /// </summary>
    public void ApplyUserDataSyncMutations(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        EnsureCacheLoaded();

        var changed = false;
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case UserDataSyncMutation.UpsertDictionary upsert:
                    changed |= UpsertSyncedDictionaryEntry(upsert.Entry);
                    break;
                case UserDataSyncMutation.DeleteDictionary delete:
                    changed |= DeleteSyncedDictionaryEntry(delete.ItemId);
                    break;
            }
        }

        if (!changed)
            return;

        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    private bool UpsertSyncedDictionaryEntry(UserDataSyncDictionaryEntry synced)
    {
        var targetType = synced.EntryType == UserDataSyncDictionaryEntryType.Term
            ? DictionaryEntryType.Term
            : DictionaryEntryType.Correction;
        var targetId = UserDataSyncIdentity.DictionaryItemId(synced.EntryType, synced.Original);
        var replacement = targetType == DictionaryEntryType.Correction ? synced.Replacement ?? string.Empty : null;

        var idx = _cache.FindIndex(entry =>
            entry.EntryType == targetType &&
            IsUserAuthored(entry) &&
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == targetId);

        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = existing with
            {
                Original = synced.Original,
                Replacement = replacement,
                CaseSensitive = synced.CaseSensitive,
                IsEnabled = synced.IsEnabled,
                UpdatedAt = synced.UpdatedAt
            };
            return true;
        }

        _cache.Add(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = targetType,
            Original = synced.Original,
            Replacement = replacement,
            CaseSensitive = synced.CaseSensitive,
            IsEnabled = synced.IsEnabled,
            CreatedAt = synced.CreatedAt,
            UpdatedAt = synced.UpdatedAt
        });
        return true;
    }

    private bool DeleteSyncedDictionaryEntry(string itemId)
    {
        var removed = _cache.RemoveAll(entry =>
            IsUserAuthored(entry) &&
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == itemId);
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
                _cache = JsonSerializer.Deserialize<List<DictionaryEntry>>(json) ?? [];
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
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    private void ReorderTerms(IReadOnlyList<DictionaryEntry> orderedTerms)
    {
        var orderedIds = orderedTerms.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var nonTerms = _cache.Where(e => e.EntryType != DictionaryEntryType.Term).ToList();
        var remainingTerms = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term && !orderedIds.Contains(e.Id))
            .ToList();

        _cache = [.. nonTerms, .. orderedTerms, .. remainingTerms];
    }

    private static IReadOnlyList<string> NormalizeTerms(IEnumerable<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Trim();
            if (term.Length == 0)
                continue;

            if (seen.Add(TermKey(term)))
                normalized.Add(term);
        }

        return normalized;
    }

    private static string TermKey(string term) => term.Trim().ToUpperInvariant();

    private static bool IsUserAuthored(DictionaryEntry entry) =>
        !entry.Id.StartsWith(PackEntryPrefix, StringComparison.Ordinal);

    private static string BuildCorrectionPattern(string original)
    {
        var pattern = Regex.Escape(original);
        return ContainsScriptWithoutWhitespaceBoundaries(original) ? pattern : @"\b" + pattern + @"\b";
    }

    private static bool ContainsScriptWithoutWhitespaceBoundaries(string text) =>
        text.Any(IsScriptWithoutWhitespaceBoundaries);

    private static bool IsScriptWithoutWhitespaceBoundaries(char ch) =>
        ch is >= '\u3040' and <= '\u30FF' // Hiragana and Katakana
            or >= '\u3400' and <= '\u9FFF' // CJK ideographs
            or >= '\uAC00' and <= '\uD7AF'; // Hangul syllables

    private static DictionaryEntry BackfillTimestamps(DictionaryEntry entry)
    {
        var createdAt = entry.CreatedAt == default ? DateTime.UtcNow : NormalizeUtc(entry.CreatedAt);
        var updatedAt = entry.UpdatedAt == default ? createdAt : NormalizeUtc(entry.UpdatedAt);
        return entry with { CreatedAt = createdAt, UpdatedAt = updatedAt };
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
