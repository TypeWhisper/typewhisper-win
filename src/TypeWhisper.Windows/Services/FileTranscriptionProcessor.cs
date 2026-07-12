using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.NumberNormalization;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Defines the file transcription processor contract.
/// </summary>
public interface IFileTranscriptionProcessor
{
    /// <summary>
    /// Transcribes an audio file, reports queue progress, and returns both raw and processed text.
    /// </summary>
    Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Represents file transcription process options data.
/// </summary>
/// <param name="EngineId">Engine id supplied to the member.</param>
/// <param name="ModelId">Model id supplied to the member.</param>
/// <param name="Language">Language supplied to the member.</param>
/// <param name="Task">Task supplied to the member.</param>
/// <param name="LanguageHints">Ordered language hints supplied to the member.</param>
public sealed record FileTranscriptionProcessOptions(
    string? EngineId = null,
    string? ModelId = null,
    string? Language = null,
    TranscriptionTask? Task = null,
    IReadOnlyList<string>? LanguageHints = null);

/// <summary>
/// Represents file transcription process progress data.
/// </summary>
/// <param name="Status">Status supplied to the member.</param>
/// <param name="StatusText">Status text supplied to the member.</param>
public sealed record FileTranscriptionProcessProgress(
    FileTranscriptionQueueItemStatus Status,
    string StatusText);

/// <summary>
/// Represents file transcription process result data.
/// </summary>
/// <param name="RawResult">Raw result supplied to the member.</param>
/// <param name="ProcessedText">Processed text supplied to the member.</param>
public sealed record FileTranscriptionProcessResult(
    TranscriptionResult RawResult,
    string ProcessedText);

/// <summary>
/// Provides file transcription processor behavior.
/// </summary>
public sealed class FileTranscriptionProcessor(
    ModelManagerService modelManager,
    ISettingsService settings,
    AudioFileService audioFile,
    IDictionaryService dictionary,
    IVocabularyBoostingService vocabularyBoosting,
    IPostProcessingPipeline pipeline) : IFileTranscriptionProcessor
{
    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken)
    {
        onProgress(new FileTranscriptionProcessProgress(
            FileTranscriptionQueueItemStatus.Loading,
            Loc.Instance["FileTranscription.LoadingAudio"]));

        await using var modelScope = await modelManager.BeginTranscriptionRequestAsync(
            options?.EngineId,
            options?.ModelId,
            false,
            cancellationToken);

        var samples = await audioFile.LoadAudioAsync(filePath, cancellationToken);

        onProgress(new FileTranscriptionProcessProgress(
            FileTranscriptionQueueItemStatus.Transcribing,
            Loc.Instance["FileTranscription.Transcribing"]));

        var currentSettings = settings.Current;
        var languageHints = options?.Language is { } configuredLanguage
            ? AppSettings.NormalizeLanguageHints([configuredLanguage])
            : options?.LanguageHints is { Count: > 0 } configuredHints
                ? AppSettings.NormalizeLanguageHints(configuredHints)
                : currentSettings.GetLanguageHints();
        var language = languageHints.FirstOrDefault();
        var task = options?.Task ?? (currentSettings.TranscriptionTask == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe);

        var activeResult = await modelManager.TranscribeActiveWithLanguageHintsAsync(
            samples,
            languageHints,
            task,
            prompt: null,
            cancellationToken);
        var result = activeResult.Result;
        var pipelineResult = await pipeline.ProcessAsync(result.Text, new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = currentSettings.TranscriptionNumberNormalizationEnabled,
            TranscriptionTask = task,
            DetectedLanguage = result.DetectedLanguage,
            ConfiguredLanguage = language,
            ConfiguredLanguageCandidates = languageHints,
            VocabularyBooster = currentSettings.VocabularyBoostingEnabled ? vocabularyBoosting.Apply : null,
            DictionaryCorrector = dictionary.ApplyCorrections
        }, cancellationToken);
        var normalizedResult = TranscriptionNumberNormalizationService.NormalizeResult(
            result,
            task,
            language,
            languageHints,
            currentSettings.TranscriptionNumberNormalizationEnabled);

        modelManager.ScheduleAutoUnload();

        return new FileTranscriptionProcessResult(normalizedResult, pipelineResult.Text);
    }
}
