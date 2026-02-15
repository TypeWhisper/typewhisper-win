using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IDictionaryService
{
    IReadOnlyList<DictionaryEntry> Entries { get; }
    event Action? EntriesChanged;

    void AddEntry(DictionaryEntry entry);
    void AddEntries(IEnumerable<DictionaryEntry> entries);
    void UpdateEntry(DictionaryEntry entry);
    void DeleteEntry(string id);
    void DeleteEntries(IEnumerable<string> ids);

    string ApplyCorrections(string text);
    string? GetTermsForPrompt();
    void LearnCorrection(string original, string replacement);

    void ActivatePack(TermPack pack);
    void DeactivatePack(string packId);
}
