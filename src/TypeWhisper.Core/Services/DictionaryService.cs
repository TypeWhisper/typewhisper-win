using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class DictionaryService : IDictionaryService
{
    private readonly string _filePath;
    private List<DictionaryEntry> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<DictionaryEntry> Entries
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? EntriesChanged;

    public DictionaryService(string filePath)
    {
        _filePath = filePath;
    }

    public void AddEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        _cache.Add(entry);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        EnsureCacheLoaded();
        _cache.AddRange(entries);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void UpdateEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0) _cache[idx] = entry;
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void DeleteEntry(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(e => e.Id == id);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        EnsureCacheLoaded();
        var idSet = ids.ToHashSet();
        _cache.RemoveAll(e => idSet.Contains(e.Id));
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

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
                var pattern = Regex.Escape(entry.Original);
                var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                text = Regex.Replace(text, @"\b" + pattern + @"\b", entry.Replacement!, options);

                IncrementUsageCount(entry.Id);
            }
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = GetEnabledTerms();

        if (terms.Count == 0) return null;
        return string.Join(", ", terms);
    }

    public IReadOnlyList<string> GetEnabledTerms()
    {
        EnsureCacheLoaded();
        return NormalizeTerms(_cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original));
    }

    public IReadOnlyList<DictionaryEntry> GetEnabledCorrections()
    {
        EnsureCacheLoaded();
        return _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction)
            .OrderBy(e => e.Original, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
                    var updated = entry with { Original = desiredTerm, IsEnabled = true };
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
        var addedTerms = normalized
            .Where(term => !existingKeys.Contains(TermKey(term)))
            .Select(term => new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Term,
                Original = term
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

    public void RemoveAllTerms()
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(e => e.EntryType == DictionaryEntryType.Term);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

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
                    IsEnabled = true
                };
            }
        }
        else
        {
            _cache.Add(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement,
                CaseSensitive = caseSensitive
            });
        }

        SaveToDisk();
        EntriesChanged?.Invoke();
    }

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
            AddEntry(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement
            });
        }
    }

    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var existingOriginals = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newEntries = pack.Terms
            .Where(t => !existingOriginals.Contains(t))
            .Select(t => new DictionaryEntry
            {
                Id = $"pack:{pack.Id}:{t}",
                EntryType = DictionaryEntryType.Term,
                Original = t
            })
            .ToList();

        if (newEntries.Count > 0)
        {
            _cache.AddRange(newEntries);
            SaveToDisk();
            EntriesChanged?.Invoke();
        }
    }

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

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<DictionaryEntry>>(json) ?? [];
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
}
