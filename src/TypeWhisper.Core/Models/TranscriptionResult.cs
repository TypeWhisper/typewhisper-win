namespace TypeWhisper.Core.Models;

public sealed record TranscriptionResult
{
    public required string Text { get; init; }
    public string? DetectedLanguage { get; init; }
    public double Duration { get; init; }
    public double ProcessingTime { get; init; }
    public IReadOnlyList<TranscriptionSegment> Segments { get; init; } = [];
}
