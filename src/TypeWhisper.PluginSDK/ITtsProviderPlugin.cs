using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Plugin that provides text-to-speech playback for spoken feedback and readback.
/// </summary>
public interface ITtsProviderPlugin : ITypeWhisperPlugin
{
    /// <summary>Unique provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Human-readable provider name for the UI.</summary>
    string ProviderDisplayName { get; }

    /// <summary>Whether the provider is configured and ready to speak.</summary>
    bool IsConfigured { get; }

    /// <summary>Available voices for this provider.</summary>
    IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; }

    /// <summary>Currently selected voice ID, or null for the provider default.</summary>
    string? SelectedVoiceId { get; }

    /// <summary>Optional summary of current provider-specific settings.</summary>
    string? SettingsSummary => null;

    /// <summary>Selects a voice by provider-specific ID, or null for the provider default.</summary>
    void SelectVoice(string? voiceId);

    /// <summary>Speaks the requested text and returns a playback session that can be stopped.</summary>
    Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct);
}
