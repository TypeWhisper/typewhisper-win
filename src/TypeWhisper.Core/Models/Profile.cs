namespace TypeWhisper.Core.Models;

public sealed record Profile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int Priority { get; init; }
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public IReadOnlyList<string> UrlPatterns { get; init; } = [];
    public string? InputLanguage { get; init; }
    public string? TranslationTarget { get; init; }
    public string? SelectedTask { get; init; }
    public bool? WhisperModeOverride { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
