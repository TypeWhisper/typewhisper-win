namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents dictionary entry data.
/// </summary>
public sealed record DictionaryEntry
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the entry type value.
    /// </summary>
    public required DictionaryEntryType EntryType { get; init; }
    /// <summary>
    /// Gets or sets the original value.
    /// </summary>
    public required string Original { get; init; }
    /// <summary>
    /// Gets or sets the replacement value.
    /// </summary>
    public string? Replacement { get; init; }
    /// <summary>
    /// Gets or sets the case sensitive value.
    /// </summary>
    public bool CaseSensitive { get; init; }
    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets the usage count value.
    /// </summary>
    public int UsageCount { get; init; }
    /// <summary>
    /// Gets or sets the created at value.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Gets or sets the updated at value.
    /// </summary>
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// Lists the supported dictionary entry type values.
/// </summary>
public enum DictionaryEntryType
{
    /// <summary>
    /// Represents the term option.
    /// </summary>
    Term,
    /// <summary>
    /// Represents the correction option.
    /// </summary>
    Correction
}
