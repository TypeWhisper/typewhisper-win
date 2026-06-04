namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents error log entry data.
/// </summary>
public sealed record ErrorLogEntry
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the timestamp value.
    /// </summary>
    public required DateTime Timestamp { get; init; }
    /// <summary>
    /// Gets or sets the message value.
    /// </summary>
    public required string Message { get; init; }
    /// <summary>
    /// Gets or sets the category value.
    /// </summary>
    public string Category { get; init; } = "general";

    /// <summary>
    /// Creates.
    /// </summary>
    public static ErrorLogEntry Create(string message, string category = "general") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = DateTime.UtcNow,
        Message = message,
        Category = category
    };
}

/// <summary>
/// Provides error category behavior.
/// </summary>
public static class ErrorCategory
{
    /// <summary>
    /// Defines the general constant.
    /// </summary>
    public const string General = "general";
    /// <summary>
    /// Defines the transcription constant.
    /// </summary>
    public const string Transcription = "transcription";
    /// <summary>
    /// Defines the recording constant.
    /// </summary>
    public const string Recording = "recording";
    /// <summary>
    /// Defines the prompt constant.
    /// </summary>
    public const string Prompt = "prompt";
    /// <summary>
    /// Defines the plugin constant.
    /// </summary>
    public const string Plugin = "plugin";
    /// <summary>
    /// Defines the insertion constant.
    /// </summary>
    public const string Insertion = "insertion";
}
