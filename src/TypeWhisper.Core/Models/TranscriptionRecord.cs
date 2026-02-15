namespace TypeWhisper.Core.Models;

public sealed record TranscriptionRecord
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string RawText { get; init; }
    public required string FinalText { get; init; }
    public string? AppName { get; init; }
    public string? AppProcessName { get; init; }
    public string? AppUrl { get; init; }
    public double DurationSeconds { get; init; }
    public string? Language { get; init; }
    public string? ProfileName { get; init; }
    public string EngineUsed { get; init; } = "whisper";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public int WordCount => FinalText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    public string Preview => FinalText.Length > 100 ? string.Concat(FinalText.AsSpan(0, 100), "...") : FinalText;
}
