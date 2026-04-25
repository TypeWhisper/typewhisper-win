namespace TypeWhisper.Core.Models;

public sealed record ErrorLogEntry
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Message { get; init; }
    public string Category { get; init; } = "general";

    public static ErrorLogEntry Create(string message, string category = "general") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = DateTime.UtcNow,
        Message = message,
        Category = category
    };
}

public static class ErrorCategory
{
    public const string General = "general";
    public const string Transcription = "transcription";
    public const string Recording = "recording";
    public const string Prompt = "prompt";
    public const string Plugin = "plugin";
    public const string Insertion = "insertion";
}
