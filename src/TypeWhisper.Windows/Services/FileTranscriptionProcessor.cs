using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Services;

public interface IFileTranscriptionProcessor
{
    Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken);
}

public sealed record FileTranscriptionProcessOptions(
    string? EngineId = null,
    string? ModelId = null,
    string? Language = null,
    TranscriptionTask? Task = null);

public sealed record FileTranscriptionProcessProgress(
    FileTranscriptionQueueItemStatus Status,
    string StatusText);

public sealed record FileTranscriptionProcessResult(
    TranscriptionResult RawResult,
    string ProcessedText);

public sealed class FileTranscriptionProcessor(
    ModelManagerService modelManager,
    ISettingsService settings,
    AudioFileService audioFile,
    IDictionaryService dictionary,
    IVocabularyBoostingService vocabularyBoosting,
    IPostProcessingPipeline pipeline) : IFileTranscriptionProcessor
{
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
        var configuredLanguage = options?.Language ?? currentSettings.Language;
        var language = configuredLanguage == "auto" ? null : configuredLanguage;
        var task = options?.Task ?? (currentSettings.TranscriptionTask == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe);

        var activeResult = await modelManager.TranscribeActiveAsync(
            samples,
            language,
            task,
            prompt: null,
            cancellationToken);
        var result = activeResult.Result;
        var pipelineResult = await pipeline.ProcessAsync(result.Text, new PipelineOptions
        {
            VocabularyBooster = currentSettings.VocabularyBoostingEnabled ? vocabularyBoosting.Apply : null,
            DictionaryCorrector = dictionary.ApplyCorrections
        }, cancellationToken);

        modelManager.ScheduleAutoUnload();

        return new FileTranscriptionProcessResult(result, pipelineResult.Text);
    }
}
