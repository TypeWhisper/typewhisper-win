using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LearnedCorrectionStore
{
    internal static IReadOnlyList<LearnedDictionaryCorrection> Save(string path, IReadOnlyList<CorrectionSuggestion> suggestions)
    {
        // One strict read decides what the dictionary holds. A file that cannot be read or parsed learns nothing;
        // a second, lenient read could see a locked file as empty and replace the dictionary with only these corrections.
        var baseline = File.Exists(path) ? File.ReadAllText(path) : null;
        var entries = baseline is null ? [] : LexiconTransfer.ReadDictionary(baseline, allowPackEntries: true).ToList();
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
            // The commit writes only while the file still matches what was read, so a concurrent change is never lost.
            try { ReviewedCatalogTransaction.Commit(path, baseline, LexiconTransfer.WriteDictionary(entries)); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            { throw new IOException("Could not save learned corrections.", ex); }
        }
        return learned;
    }
}
