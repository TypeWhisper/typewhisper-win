using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Lists supported recorder output containers.
/// </summary>
public enum RecorderOutputFormat
{
    /// <summary>
    /// Represents the WAV option.
    /// </summary>
    Wav,
    /// <summary>
    /// Represents the M4A option.
    /// </summary>
    M4A
}

/// <summary>
/// Lists supported recorder track layouts.
/// </summary>
public enum RecorderTrackMode
{
    /// <summary>
    /// Represents a mono mixdown.
    /// </summary>
    Mixed,
    /// <summary>
    /// Represents a stereo file with microphone on the left and system audio on the right.
    /// </summary>
    Separate
}

/// <summary>
/// Lists microphone ducking profiles used while system audio is present.
/// </summary>
public enum RecorderMicDuckingMode
{
    /// <summary>
    /// Represents stronger microphone ducking.
    /// </summary>
    Aggressive,
    /// <summary>
    /// Represents moderate microphone ducking.
    /// </summary>
    Medium,
    /// <summary>
    /// Represents no microphone ducking.
    /// </summary>
    Off
}

/// <summary>
/// Lists recorder API session states.
/// </summary>
public enum RecorderSessionStatus
{
    /// <summary>
    /// Represents an active recording.
    /// </summary>
    Recording,
    /// <summary>
    /// Represents finalization after recording stopped.
    /// </summary>
    Finalizing,
    /// <summary>
    /// Represents a completed recording and transcription.
    /// </summary>
    Completed,
    /// <summary>
    /// Represents a failed recording session.
    /// </summary>
    Failed
}

/// <summary>
/// Describes a recorder capture request.
/// </summary>
public sealed record RecorderCaptureOptions(
    bool MicEnabled,
    bool SystemAudioEnabled,
    string? SystemAudioDeviceId,
    RecorderOutputFormat OutputFormat,
    RecorderTrackMode TrackMode,
    RecorderMicDuckingMode MicDuckingMode);

/// <summary>
/// Represents an output device that can be used for system-audio loopback capture.
/// </summary>
public sealed record SystemAudioOutputDevice(string? Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Represents a saved recorder capture.
/// </summary>
public sealed record RecorderCaptureResult(
    string FilePath,
    string FileName,
    float[] TranscriptionSamples,
    TimeSpan Duration);

/// <summary>
/// Represents a recorder API session snapshot.
/// </summary>
public sealed record RecorderApiSessionSnapshot(
    Guid Id,
    RecorderSessionStatus Status,
    string? Text,
    string? OutputFile,
    string? Error);

/// <summary>
/// API-facing recorder control surface.
/// </summary>
public interface IRecorderApiController
{
    /// <summary>
    /// Gets whether recording is active.
    /// </summary>
    bool IsRecording { get; }

    /// <summary>
    /// Starts recorder capture for the local API.
    /// </summary>
    Task<Guid> StartRecordingForApiAsync(
        bool micEnabled,
        bool systemAudioEnabled,
        CancellationToken ct);

    /// <summary>
    /// Stops recorder capture for the local API.
    /// </summary>
    Task<Guid?> StopRecordingForApiAsync(CancellationToken ct);

    /// <summary>
    /// Returns a recorder API session by id.
    /// </summary>
    RecorderApiSessionSnapshot? GetRecorderApiSession(Guid id);
}

/// <summary>
/// Minimal audio source consumed by live streaming transcription.
/// </summary>
public interface IStreamingAudioSource
{
    /// <summary>
    /// Raised when normalized 16 kHz mono samples are available.
    /// </summary>
    event EventHandler<SamplesAvailableEventArgs>? SamplesAvailable;

    /// <summary>
    /// Gets the peak RMS level for the active capture.
    /// </summary>
    float PeakRmsLevel { get; }

    /// <summary>
    /// Returns the current normalized 16 kHz mono buffer.
    /// </summary>
    float[]? GetCurrentBuffer();
}

/// <summary>
/// Normalizes recorder settings values.
/// </summary>
public static class RecorderSettings
{
    /// <summary>
    /// Parses an output format string.
    /// </summary>
    public static RecorderOutputFormat ParseOutputFormat(string? value) =>
        string.Equals(value, "m4a", StringComparison.OrdinalIgnoreCase)
            ? RecorderOutputFormat.M4A
            : RecorderOutputFormat.Wav;

    /// <summary>
    /// Parses a track mode string.
    /// </summary>
    public static RecorderTrackMode ParseTrackMode(string? value) =>
        string.Equals(value, "separate", StringComparison.OrdinalIgnoreCase)
            ? RecorderTrackMode.Separate
            : RecorderTrackMode.Mixed;

    /// <summary>
    /// Parses a ducking mode string.
    /// </summary>
    public static RecorderMicDuckingMode ParseDuckingMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "medium" => RecorderMicDuckingMode.Medium,
            "off" => RecorderMicDuckingMode.Off,
            _ => RecorderMicDuckingMode.Aggressive
        };

    /// <summary>
    /// Returns a settings string for an output format.
    /// </summary>
    public static string ToSettingsValue(RecorderOutputFormat value) =>
        value == RecorderOutputFormat.M4A ? "m4a" : "wav";

    /// <summary>
    /// Returns a settings string for a track mode.
    /// </summary>
    public static string ToSettingsValue(RecorderTrackMode value) =>
        value == RecorderTrackMode.Separate ? "separate" : "mixed";

    /// <summary>
    /// Returns a settings string for a ducking mode.
    /// </summary>
    public static string ToSettingsValue(RecorderMicDuckingMode value) =>
        value switch
        {
            RecorderMicDuckingMode.Medium => "medium",
            RecorderMicDuckingMode.Off => "off",
            _ => "aggressive"
        };
}
