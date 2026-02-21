namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Base class for all plugin events published via the event bus.
/// </summary>
public abstract record PluginEvent
{
    /// <summary>UTC timestamp when the event was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Raised when audio recording starts.</summary>
public sealed record RecordingStartedEvent : PluginEvent;

/// <summary>Raised when audio recording stops.</summary>
public sealed record RecordingStoppedEvent : PluginEvent
{
    /// <summary>Duration of the recording in seconds.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>Raised after a successful transcription.</summary>
public sealed record TranscriptionCompletedEvent : PluginEvent
{
    /// <summary>The transcribed text.</summary>
    public required string Text { get; init; }

    /// <summary>Detected language (ISO code), or null.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Audio duration in seconds.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Model ID used for transcription, or null.</summary>
    public string? ModelId { get; init; }
}

/// <summary>Raised when transcription fails.</summary>
public sealed record TranscriptionFailedEvent : PluginEvent
{
    /// <summary>Error message describing the failure.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Model ID that was being used, or null.</summary>
    public string? ModelId { get; init; }
}

/// <summary>Raised after text is inserted into the target application.</summary>
public sealed record TextInsertedEvent : PluginEvent
{
    /// <summary>The text that was inserted.</summary>
    public required string Text { get; init; }

    /// <summary>Name of the target application, or null.</summary>
    public string? TargetApp { get; init; }
}
