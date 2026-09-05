using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

internal enum PrototypeLexiconKind { Word, Correction, Snippet }

// Dictionary entries use isolated development storage; snippets remain session-only.
internal sealed record PrototypeLexiconEntry(Guid Id, PrototypeLexiconKind Kind, string Key,
    string Value = "", string Tags = "", bool CaseSensitive = false, bool Enabled = true, bool FromPack = false);

internal sealed class PrototypeLexicon
{
    private readonly List<PrototypeLexiconEntry> _entries = [];
    private readonly DictionaryService? _dictionary;
    private string? _loadError;
    internal string? LastError { get; private set; }
    internal PrototypeLexicon(string? dictionaryPath = null)
    {
        if (dictionaryPath is null) return;
        try
        {
            // Fail closed: do not overwrite a malformed dictionary with an empty cache.
            if (File.Exists(dictionaryPath))
            {
                var entries = JsonSerializer.Deserialize<List<DictionaryEntry>>(File.ReadAllText(dictionaryPath));
                if (entries is null || entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Original)))
                    throw new JsonException("Invalid dictionary entries.");
            }
            _dictionary = new(dictionaryPath);
            RefreshDictionary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LastError = _loadError = "Dictionary could not be loaded: " + ex.Message; }
    }

    private static Guid UiId(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16));
    private void RefreshDictionary()
    {
        if (_dictionary is null) return;
        _entries.RemoveAll(e => e.Kind != PrototypeLexiconKind.Snippet);
        _entries.AddRange(_dictionary.Entries.Select(e => new PrototypeLexiconEntry(UiId(e.Id),
            e.EntryType == DictionaryEntryType.Term ? PrototypeLexiconKind.Word : PrototypeLexiconKind.Correction,
            e.Original, e.Replacement ?? "", e.Id.StartsWith("pack:", StringComparison.Ordinal) ? "Term pack" : "",
            e.CaseSensitive, e.IsEnabled, e.Id.StartsWith("pack:", StringComparison.Ordinal))));
    }

    internal bool PackEnabled(string id) => _dictionary?.Entries.Any(e => e.Id.StartsWith($"pack:{id}:", StringComparison.Ordinal)) == true;
    internal string? SetPackEnabled(TermPack pack, bool enabled)
    {
        if (_loadError is not null) return LastError = _loadError;
        if (_dictionary is null) return LastError = "Persistent dictionary is unavailable.";
        if (pack.RequiresCommercialLicense) return LastError = "This pack requires commercial license integration.";
        var prefix = $"pack:{pack.Id}:";
        var updated = _dictionary.Entries.Where(e => !e.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        // Keep per-pack ownership even when multiple packs contain the same word.
        // Removing one pack must not remove a personal term or another pack's term.
        if (enabled) updated.AddRange(pack.Terms.Distinct(StringComparer.OrdinalIgnoreCase).Select(term => new DictionaryEntry
        { Id = prefix + term, EntryType = DictionaryEntryType.Term, Original = term }));
        if (!_dictionary.TryReplaceAll(updated)) return LastError = "Could not save term pack selection.";
        RefreshDictionary(); LastError = null; return null;
    }
    internal IReadOnlyList<PrototypeLexiconEntry> Entries => _entries.AsReadOnly();

    internal IEnumerable<PrototypeLexiconEntry> Search(PrototypeLexiconKind kind, string query) =>
        _entries.Where(entry => entry.Kind == kind &&
            string.Join(' ', entry.Key, entry.Value, entry.Tags).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    internal string? Save(PrototypeLexiconEntry draft)
    {
        if (_loadError is not null && draft.Kind != PrototypeLexiconKind.Snippet) return LastError = _loadError;
        if (draft.FromPack) return "Manage this term through its term pack.";
        var key = draft.Key.Trim();
        if (key.Length == 0) return draft.Kind == PrototypeLexiconKind.Snippet ? "Enter a trigger phrase." : "Enter a word or phrase.";
        if (key.Length > 160 || key.Contains('\n') || key.Contains('\r')) return "Use a single line of up to 160 characters.";
        if (draft.Kind != PrototypeLexiconKind.Word && string.IsNullOrWhiteSpace(draft.Value)) return "Enter the replacement text.";
        if (draft.Value.Length > 10000) return "Keep the replacement below 10,001 characters.";
        if (draft.Tags.Length > 300) return "Keep tags below 301 characters.";
        if (draft.Kind == PrototypeLexiconKind.Correction && key.Equals(draft.Value.Trim(), StringComparison.Ordinal)) return "The correction must differ from the original phrase.";
        if (_entries.Any(entry => entry.Id != draft.Id && entry.Kind == draft.Kind &&
            entry.Key.Equals(key, entry.CaseSensitive && draft.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
            return "This word or trigger already exists in this section.";
        var normalized = draft with { Key = key, Value = draft.Kind == PrototypeLexiconKind.Word ? "" : draft.Value, Tags = draft.Tags.Trim() };
        if (_dictionary is not null && draft.Kind != PrototypeLexiconKind.Snippet)
        {
            var existing = _dictionary.Entries.FirstOrDefault(e => UiId(e.Id) == draft.Id);
            var entry = (existing ?? new DictionaryEntry { Id = draft.Id.ToString(), EntryType = draft.Kind == PrototypeLexiconKind.Word ? DictionaryEntryType.Term : DictionaryEntryType.Correction, Original = key })
                with { Original = key, Replacement = draft.Kind == PrototypeLexiconKind.Word ? null : draft.Value, CaseSensitive = draft.CaseSensitive, IsEnabled = draft.Enabled, UpdatedAt = DateTime.UtcNow };
            if (!_dictionary.TryReplaceAll(_dictionary.Entries.Where(e => e.Id != entry.Id).Append(entry).ToArray())) return LastError = "Could not save dictionary entry.";
            RefreshDictionary(); LastError = null; return null;
        }
        var index = _entries.FindIndex(entry => entry.Id == draft.Id);
        if (index < 0) _entries.Add(normalized); else _entries[index] = normalized;
        return null;
    }

    internal bool Remove(Guid id)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.FromPack) return false;
        if (entry.Kind != PrototypeLexiconKind.Snippet && _dictionary is not null)
        {
            if (!_dictionary.TryReplaceAll(_dictionary.Entries.Where(e => UiId(e.Id) != id).ToArray())) { LastError = "Could not delete entry."; return false; }
            RefreshDictionary(); LastError = null; return true;
        }
        return _entries.RemoveAll(e => e.Id == id) == 1;
    }

    internal static PrototypeLexicon CreateSamples()
    {
        var result = new PrototypeLexicon();
        foreach (var word in new[] { "TypeWhisper", "WinUI", "Parakeet" }) result.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, word));
        result.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Correction, "type whisper", "TypeWhisper"));
        result.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Correction, "get hub", "GitHub"));
        result.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "my signature", "Best regards,\nAlex", "email, personal"));
        result.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "meeting notes", "Meeting: {date}\n\nSummary\n\nNext steps", "work"));
        result.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "quick thanks", "Thanks for your message. I'll get back to you shortly.", "email"));
        return result;
    }
}
