using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the dictionary service contract.
/// </summary>
public interface IDictionaryService
{
    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    IReadOnlyList<DictionaryEntry> Entries { get; }
    /// <summary>
    /// Raised when entries changes.
    /// </summary>
    event Action? EntriesChanged;

    /// <summary>
    /// Adds a dictionary entry and persists the updated dictionary.
    /// </summary>
    void AddEntry(DictionaryEntry entry);
    /// <summary>
    /// Adds entries.
    /// </summary>
    void AddEntries(IEnumerable<DictionaryEntry> entries);
    /// <summary>
    /// Updates entry.
    /// </summary>
    void UpdateEntry(DictionaryEntry entry);
    /// <summary>
    /// Deletes entry.
    /// </summary>
    void DeleteEntry(string id);
    /// <summary>
    /// Deletes entries.
    /// </summary>
    void DeleteEntries(IEnumerable<string> ids);

    /// <summary>
    /// Applies corrections.
    /// </summary>
    string ApplyCorrections(string text);
    /// <summary>
    /// Returns terms for prompt.
    /// </summary>
    string? GetTermsForPrompt();
    /// <summary>
    /// Returns enabled terms.
    /// </summary>
    IReadOnlyList<string> GetEnabledTerms() => Entries
        .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
        .Select(e => e.Original)
        .ToList();
    /// <summary>
    /// Returns enabled corrections.
    /// </summary>
    IReadOnlyList<DictionaryEntry> GetEnabledCorrections() => Entries
        .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction)
        .ToList();

    /// <summary>
    /// Sets terms.
    /// </summary>
    void SetTerms(IEnumerable<string> terms, bool replaceExisting) =>
        throw new NotSupportedException();

    /// <summary>
    /// Removes all terms.
    /// </summary>
    void RemoveAllTerms() =>
        throw new NotSupportedException();

    /// <summary>
    /// Deletes term.
    /// </summary>
    bool DeleteTerm(string term) =>
        throw new NotSupportedException();

    /// <summary>
    /// Upserts correction.
    /// </summary>
    void UpsertCorrection(string original, string replacement, bool caseSensitive) =>
        throw new NotSupportedException();

    /// <summary>
    /// Deletes correction.
    /// </summary>
    bool DeleteCorrection(string original) =>
        throw new NotSupportedException();

    /// <summary>
    /// Learns correction.
    /// </summary>
    void LearnCorrection(string original, string replacement);

    /// <summary>
    /// Activates pack.
    /// </summary>
    void ActivatePack(TermPack pack);
    /// <summary>
    /// Deactivates pack.
    /// </summary>
    void DeactivatePack(string packId);
}
