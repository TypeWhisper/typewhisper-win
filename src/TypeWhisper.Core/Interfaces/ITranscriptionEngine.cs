using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface ITranscriptionEngine
{
    bool IsModelLoaded { get; }

    Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default);
    void UnloadModel();

    Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default);
}

public enum TranscriptionTask
{
    Transcribe,
    Translate
}
