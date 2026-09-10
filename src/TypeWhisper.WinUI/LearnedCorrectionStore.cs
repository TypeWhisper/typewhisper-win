using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LearnedCorrectionStore
{
    internal static IReadOnlyList<LearnedDictionaryCorrection> Save(string path, IReadOnlyList<CorrectionSuggestion> suggestions)
    {
        if (File.Exists(path)) _ = LexiconTransfer.ReadDictionary(File.ReadAllText(path), allowPackEntries: true);
        var dictionary = new DictionaryService(path);
        var entries = dictionary.Entries.ToList();
        var originals = entries.Where(e => e.EntryType == DictionaryEntryType.Correction).Select(e => e.Original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var learned = new List<LearnedDictionaryCorrection>();
        foreach (var suggestion in suggestions)
        {
            if (!originals.Add(suggestion.Original)) continue;
            var entry = new DictionaryEntry { Id = Guid.NewGuid().ToString(), Original = suggestion.Original, Replacement = suggestion.Replacement,
                EntryType = DictionaryEntryType.Correction, Source = DictionaryEntrySource.AutoLearned, UpdatedAt = DateTime.UtcNow };
            entries.Add(entry); learned.Add(new(entry.Id, entry.Original, entry.Replacement));
        }
        if (learned.Count > 0)
        {
            if (!dictionary.TryReplaceAll(entries)) throw new IOException("Could not save learned corrections.");
        }
        return learned;
    }
}
