namespace TypeWhisper.WinUI;

internal enum LexiconKind { Word, Correction, Snippet }

// Session-only preview data. Never reads or writes the production dictionary.
internal sealed record LexiconEntry(Guid Id, LexiconKind Kind, string Key,
    string Value = "", string Tags = "", bool CaseSensitive = false, bool Enabled = true);

internal sealed class Lexicon
{
    private readonly List<LexiconEntry> _entries = [];
    internal IReadOnlyList<LexiconEntry> Entries => _entries.AsReadOnly();

    internal IEnumerable<LexiconEntry> Search(LexiconKind kind, string query) =>
        _entries.Where(entry => entry.Kind == kind &&
            string.Join(' ', entry.Key, entry.Value, entry.Tags).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    internal string? Save(LexiconEntry draft)
    {
        var key = draft.Key.Trim();
        if (key.Length == 0) return draft.Kind == LexiconKind.Snippet ? "Enter a trigger phrase." : "Enter a word or phrase.";
        if (key.Length > 160 || key.Contains('\n') || key.Contains('\r')) return "Use a single line of up to 160 characters.";
        if (draft.Kind != LexiconKind.Word && string.IsNullOrWhiteSpace(draft.Value)) return "Enter the replacement text.";
        if (draft.Value.Length > 10000) return "Keep the replacement below 10,001 characters.";
        if (draft.Tags.Length > 300) return "Keep tags below 301 characters.";
        if (draft.Kind == LexiconKind.Correction && key.Equals(draft.Value.Trim(), StringComparison.Ordinal)) return "The correction must differ from the original phrase.";
        if (_entries.Any(entry => entry.Id != draft.Id && entry.Kind == draft.Kind &&
            entry.Key.Equals(key, entry.CaseSensitive && draft.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
            return "This word or trigger already exists in this section.";
        var normalized = draft with { Key = key, Value = draft.Kind == LexiconKind.Word ? "" : draft.Value, Tags = draft.Tags.Trim() };
        var index = _entries.FindIndex(entry => entry.Id == draft.Id);
        if (index < 0) _entries.Add(normalized); else _entries[index] = normalized;
        return null;
    }

    internal bool Remove(Guid id) => _entries.RemoveAll(entry => entry.Id == id) == 1;

    internal static Lexicon CreateSamples()
    {
        var result = new Lexicon();
        foreach (var word in new[] { "TypeWhisper", "WinUI", "Parakeet" }) result.Save(new(Guid.NewGuid(), LexiconKind.Word, word));
        result.Save(new(Guid.NewGuid(), LexiconKind.Correction, "type whisper", "TypeWhisper"));
        result.Save(new(Guid.NewGuid(), LexiconKind.Correction, "get hub", "GitHub"));
        result.Save(new(Guid.NewGuid(), LexiconKind.Snippet, "my signature", "Best regards,\nAlex", "email, personal"));
        result.Save(new(Guid.NewGuid(), LexiconKind.Snippet, "meeting notes", "Meeting: {date}\n\nSummary\n\nNext steps", "work"));
        result.Save(new(Guid.NewGuid(), LexiconKind.Snippet, "quick thanks", "Thanks for your message. I'll get back to you shortly.", "email"));
        return result;
    }
}
