namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes why text-to-speech playback is being requested.
/// </summary>
public enum TtsPurpose
{
    /// <summary>
    /// Represents the status option.
    /// </summary>
    Status,
    /// <summary>
    /// Represents the transcription option.
    /// </summary>
    Transcription,
    /// <summary>
    /// Represents the manual readback option.
    /// </summary>
    ManualReadback
}
