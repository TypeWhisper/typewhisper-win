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

    /// <summary>Whether this engine supports downloading and managing local model files.</summary>
    bool SupportsModelDownload => false;

    /// <summary>Whether the given model's files are downloaded and ready to use.</summary>
    bool IsModelDownloaded(string modelId) => true;

    /// <summary>Downloads model files for the given model ID, reporting progress 0.0–1.0.</summary>
    Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Loads a downloaded model into memory, preparing it for transcription.</summary>
    Task LoadModelAsync(string modelId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Whether this engine supports real-time streaming transcription via <see cref="IStreamingSession"/>.</summary>
    bool SupportsStreaming => false;

    /// <summary>
    /// Opens a real-time streaming session (e.g. WebSocket). The host feeds PCM16 audio via the session.
    /// Only called when <see cref="SupportsStreaming"/> is true.
    /// </summary>
    Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        => throw new NotSupportedException();

    /// <summary>Unloads the currently loaded model from memory to free resources.</summary>
    Task UnloadModelAsync() => Task.CompletedTask;

    /// <summary>ISO language codes supported by this engine, or empty for all.</summary>
    IReadOnlyList<string> SupportedLanguages => [];

    /// <summary>
    /// Transcribes audio with streaming progress updates. Default delegates to <see cref="TranscribeAsync"/>.
    /// </summary>
    /// <param name="wavAudio">WAV audio data to transcribe.</param>
    /// <param name="language">Target language code, or null for auto-detect.</param>
    /// <param name="translate">Whether to translate the result to English.</param>
    /// <param name="prompt">Optional prompt/context hint for the engine.</param>
    /// <param name="onProgress">Callback invoked with partial transcription text. Return false to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt,
        Func<string, bool> onProgress, CancellationToken ct)
        => TranscribeAsync(wavAudio, language, translate, prompt, ct);
}
