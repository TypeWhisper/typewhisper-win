using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Plugin that provides audio transcription capabilities via a cloud or local engine.
/// </summary>
public interface ITranscriptionEnginePlugin : ITypeWhisperPlugin
{
    /// <summary>Unique provider identifier (e.g. "openai", "groq").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable provider name for the UI.</summary>
    string ProviderDisplayName { get; }

    /// <summary>Whether the provider is configured and ready (API key set, etc.).</summary>
    bool IsConfigured { get; }

    /// <summary>Available transcription models for this provider.</summary>
    IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }

    /// <summary>Currently selected model ID, or null if none selected.</summary>
    string? SelectedModelId { get; }

    /// <summary>Whether this provider supports translation (audio to English).</summary>
    bool SupportsTranslation { get; }

    /// <summary>Selects a transcription model by ID.</summary>
    void SelectModel(string modelId);

    /// <summary>Transcribes WAV audio data and returns the result.</summary>
    Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct);
}
