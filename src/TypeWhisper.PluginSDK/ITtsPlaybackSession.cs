namespace TypeWhisper.PluginSDK;

/// <summary>
/// Represents an active text-to-speech playback session.
/// </summary>
public interface ITtsPlaybackSession
{
    /// <summary>Whether playback is still active.</summary>
    bool IsActive { get; }

    /// <summary>Raised when playback finishes or is stopped.</summary>
    event EventHandler? Completed;

    /// <summary>Stops playback if it is still active.</summary>
    void Stop();
}
