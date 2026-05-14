namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Base class for all plugin events published via the event bus.
/// </summary>
public abstract record PluginEvent
{
    /// <summary>UTC timestamp when the event was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Identifies the recording session this event belongs to.
    /// Set by the publisher on <see cref="RecordingStartedEvent"/> and carried through
    /// all related events (partial, stopped, completed, failed) for the same recording.
    /// Null for events that are not tied to a specific recording.
    /// </summary>
    public Guid? RecordingId { get; init; }
}

/// <summary>Raised when audio recording starts.</summary>
public sealed record RecordingStartedEvent : PluginEvent
{
    /// <summary>Name of the foreground application.</summary>
    public string? AppName { get; init; }

    /// <summary>Process name of the foreground application.</summary>
    public string? AppProcessName { get; init; }
}

/// <summary>Raised when audio recording stops.</summary>
public sealed record RecordingStoppedEvent : PluginEvent
{
    /// <summary>Duration of the recording in seconds.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>Raised after a successful transcription.</summary>
public sealed record TranscriptionCompletedEvent : PluginEvent
{
    /// <summary>The raw transcribed text (before post-processing).</summary>
    public string? RawText { get; init; }

    /// <summary>The final text (after post-processing).</summary>
    public required string Text { get; init; }

    /// <summary>Detected language (ISO code), or null.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Audio duration in seconds.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Engine used for transcription.</summary>
    public string? EngineUsed { get; init; }

    /// <summary>Model ID used for transcription, or null.</summary>
    public string? ModelId { get; init; }

    /// <summary>Name of the dictation profile used, or null.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Name of the foreground application.</summary>
    public string? AppName { get; init; }

    /// <summary>Process name of the foreground application.</summary>
    public string? AppProcessName { get; init; }

    /// <summary>URL of the active browser tab, or null.</summary>
    public string? Url { get; init; }
}

/// <summary>Raised when transcription fails.</summary>
public sealed record TranscriptionFailedEvent : PluginEvent
{
    /// <summary>Error message describing the failure.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Model ID that was being used, or null.</summary>
    public string? ModelId { get; init; }

    /// <summary>Name of the foreground application.</summary>
    public string? AppName { get; init; }
}

/// <summary>Raised after text is inserted into the target application.</summary>
public sealed record TextInsertedEvent : PluginEvent
{
    /// <summary>The text that was inserted.</summary>
    public required string Text { get; init; }

    /// <summary>Name of the target application, or null.</summary>
    public string? TargetApp { get; init; }
}

/// <summary>Raised when partial transcription text is updated during recording.</summary>
public sealed record PartialTranscriptionUpdateEvent : PluginEvent
{
    /// <summary>The current partial transcription text.</summary>
    public required string PartialText { get; init; }

    /// <summary>Whether recording is still in progress.</summary>
    public bool IsRecording { get; init; } = true;

    /// <summary>Elapsed seconds since recording started.</summary>
    public double ElapsedSeconds { get; init; }
}

/// <summary>Raised when an action plugin completes execution.</summary>
public sealed record ActionCompletedEvent : PluginEvent
{
    /// <summary>ID of the action that completed.</summary>
    public required string ActionId { get; init; }

    /// <summary>Whether the action succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Result message from the action.</summary>
    public string? Message { get; init; }

    /// <summary>Name of the foreground application.</summary>
    public string? AppName { get; init; }
}
