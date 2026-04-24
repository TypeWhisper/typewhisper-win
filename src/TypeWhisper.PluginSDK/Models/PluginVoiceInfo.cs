namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes a voice exposed by a text-to-speech provider.
/// </summary>
/// <param name="Id">Provider-specific voice identifier.</param>
/// <param name="DisplayName">Human-readable voice name for the UI.</param>
/// <param name="LocaleIdentifier">Optional locale identifier such as "en-US".</param>
public sealed record PluginVoiceInfo(
    string Id,
    string DisplayName,
    string? LocaleIdentifier = null);
