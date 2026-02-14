namespace TypeWhisper.Core.Models;

public sealed record TermPack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Terms { get; init; } = [];
}
