using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the transcription engine contract.
/// </summary>
public interface ITranscriptionEngine
{
    /// <summary>
    /// Gets whether a transcription model is currently loaded.
    /// </summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// Loads the selected transcription model into memory.
    /// </summary>
    Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default);
    /// <summary>
    /// Unloads the active transcription model from memory.
    /// </summary>
    void UnloadModel();

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcribes PCM audio using ordered language hints.
    /// </summary>
    Task<TranscriptionResult> TranscribeWithLanguageHintsAsync(
        float[] audioSamples,
        IReadOnlyList<string> languageHints,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default) =>
        TranscribeAsync(audioSamples, languageHints.FirstOrDefault(), task, cancellationToken);
}

/// <summary>
/// Lists the supported transcription task values.
/// </summary>
public enum TranscriptionTask
{
    /// <summary>
    /// Represents the transcribe option.
    /// </summary>
    Transcribe,
    /// <summary>
    /// Represents the translate option.
    /// </summary>
    Translate
}
