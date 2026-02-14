namespace TypeWhisper.Core.Models;

public sealed record DictionaryEntry
{
    public required string Id { get; init; }
    public required DictionaryEntryType EntryType { get; init; }
    public required string Original { get; init; }
    public string? Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int UsageCount { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public enum DictionaryEntryType
{
    Term,
    Correction
}
