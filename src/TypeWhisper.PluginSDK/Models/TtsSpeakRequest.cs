namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Text-to-speech playback request passed from the host to a TTS provider plugin.
/// </summary>
/// <param name="Text">Text to speak.</param>
/// <param name="Language">Optional BCP-47/ISO language hint.</param>
/// <param name="Purpose">Why the host is requesting playback.</param>
public sealed record TtsSpeakRequest(
    string Text,
    string? Language = null,
    TtsPurpose Purpose = TtsPurpose.Status)
{
    /// <summary>Per-request voice override; does not change the saved provider voice.</summary>
    public string? VoiceId { get; init; }
    /// <summary>Selected Windows audio endpoint, or null for the system default.</summary>
    public string? OutputDeviceId { get; init; }
}
