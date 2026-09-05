namespace TypeWhisper.WinUI;

internal enum PrototypeLexiconKind { Word, Correction, Snippet }

// Session-only preview data. Never reads or writes the production dictionary.
internal sealed record PrototypeLexiconEntry(Guid Id, PrototypeLexiconKind Kind, string Key,
    string Value = "", string Tags = "", bool CaseSensitive = false, bool Enabled = true);

internal sealed class PrototypeLexicon
{
    private readonly List<PrototypeLexiconEntry> _entries = [];
    internal IReadOnlyList<PrototypeLexiconEntry> Entries => _entries.AsReadOnly();

    internal IEnumerable<PrototypeLexiconEntry> Search(PrototypeLexiconKind kind, string query) =>
        _entries.Where(entry => entry.Kind == kind &&
            string.Join(' ', entry.Key, entry.Value, entry.Tags).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    internal string? Save(PrototypeLexiconEntry draft)
    {
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
        var index = _entries.FindIndex(entry => entry.Id == draft.Id);
        if (index < 0) _entries.Add(normalized); else _entries[index] = normalized;
        return null;
    }

    internal bool Remove(Guid id) => _entries.RemoveAll(entry => entry.Id == id) == 1;

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
