namespace TypeWhisper.Core.Models;

public sealed record Snippet
{
    public required string Id { get; init; }
    public required string Trigger { get; init; }
    public required string Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int UsageCount { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string Tags { get; init; } = "";
}
