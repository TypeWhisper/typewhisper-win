using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

// No persisted writes: an older recording must never overwrite newer UI edits.
internal sealed class DictationDictionarySnapshot
{
    internal static string StoragePath => WinUIProfile.DataPath("dictionary.json");
    private readonly DictionaryEntry[] _entries;
    private readonly TypeWhisper.Core.Interfaces.IVocabularyBoostingService _boosting;
    private readonly DictionaryService.CorrectionSet _corrections;
    internal string? Error { get; }
    internal IReadOnlyList<DictionaryEntry> EnabledCtcEntries => _entries.Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term).ToArray();
    // Loaded off the UI thread while recording, so correction regexes are ready before processing.
    private DictationDictionarySnapshot(DictionaryEntry[] entries, string? error = null)
    {
        _entries = entries; Error = error;
        _boosting = VocabularyBoostingService.CreateSnapshot(entries);
        _corrections = DictionaryService.CompileCorrections(entries);
    }
    internal IReadOnlyList<string> EnabledTerms => _entries.Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
        .Select(e => e.Original).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static DictationDictionarySnapshot Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new([]);
            var entries = JsonSerializer.Deserialize<DictionaryEntry[]>(File.ReadAllText(path));
            if (entries is null || entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Original)))
                throw new JsonException();
            return new(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new([], "Dictionary unavailable · original transcript retained."); }
    }

    internal string ApplyBoosting(string text) => _boosting.Apply(text);
    internal string ApplyCorrections(string text) => _corrections.Apply(text);

    internal string Apply(string rawText, bool boostVocabulary = false)
    {
        // This is the existing Windows text heuristic, not acoustic CTC scoring.
        var boosted = boostVocabulary ? _boosting.Apply(rawText) : rawText;
        return _corrections.Apply(boosted);
    }
}
