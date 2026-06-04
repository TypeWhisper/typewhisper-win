namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents snippet data.
/// </summary>
public sealed record Snippet
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the trigger value.
    /// </summary>
    public required string Trigger { get; init; }
    /// <summary>
    /// Gets or sets the replacement value.
    /// </summary>
    public required string Replacement { get; init; }
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
    /// <summary>
    /// Gets or sets the tags value.
    /// </summary>
    public string Tags { get; init; } = "";
}
