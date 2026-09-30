using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI.Platform;

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
/// Represents an output device that can be used for system-audio loopback capture.
/// </summary>
public sealed record SystemAudioOutputDevice(string? Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
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
