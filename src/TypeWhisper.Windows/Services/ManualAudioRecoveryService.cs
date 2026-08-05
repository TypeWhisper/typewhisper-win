using System.IO;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Transcribes one durable recovery WAV and commits it to History before deleting the source.
/// </summary>
public sealed class ManualAudioRecoveryService
{
    private readonly DictationRecoveryAudioStore _store;
    private readonly IFileTranscriptionProcessor _processor;
    private readonly IHistoryService _history;
    private readonly string _historyAudioPath;

    /// <summary>
    /// Creates a manual recovery service.
    /// </summary>
    public ManualAudioRecoveryService(
        DictationRecoveryAudioStore store,
        IFileTranscriptionProcessor processor,
        IHistoryService history)
        : this(store, processor, history, TypeWhisperEnvironment.AudioPath)
    {
    }

    internal ManualAudioRecoveryService(
        DictationRecoveryAudioStore store,
        IFileTranscriptionProcessor processor,
        IHistoryService history,
        string historyAudioPath)
    {
        _store = store;
        _processor = processor;
        _history = history;
        _historyAudioPath = historyAudioPath;
    }

    /// <summary>
    /// Runs a labeled manual recovery action independent of automatic History settings.
    /// </summary>
    public async Task<TranscriptionRecord> RecoverAsync(
        string recoveryId,
        FileTranscriptionProcessOptions options,
        Action<FileTranscriptionProcessProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var descriptor = _store.Recordings.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, recoveryId, StringComparison.OrdinalIgnoreCase));
        var sourcePath = descriptor is null ? null : _store.GetRecordingPath(descriptor.Id);
        if (descriptor is null || sourcePath is null)
            throw new FileNotFoundException("The selected recovery recording is no longer available.");

        var result = await _processor.ProcessAsync(
            sourcePath,
            onProgress,
            options,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.RawResult.Text)
            || string.IsNullOrWhiteSpace(result.ProcessedText))
        {
            throw new InvalidOperationException("The recovery transcription returned an empty response.");
        }

        var audioFileName = await CopyHistoryAudioAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var record = new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            RawText = result.RawResult.Text,
            FinalText = result.ProcessedText,
            DurationSeconds = result.RawResult.Duration > 0 ? result.RawResult.Duration : descriptor.DurationSeconds,
            Language = result.RawResult.DetectedLanguage,
            EngineUsed = result.EngineId ?? options.EngineId ?? "unknown",
            ModelUsed = result.ModelId ?? options.ModelId,
            AudioFileName = audioFileName,
            TranscriptionTaskUsed = result.Task.ToString(),
            Status = TranscriptionRecordStatus.Succeeded
        };

        if (!_history.TryAddRecord(record))
        {
            DeleteHistoryAudio(audioFileName);
            throw new IOException("The recovered transcription could not be saved to history.");
        }

        if (!await _store.DeleteAsync(descriptor.Id, CancellationToken.None).ConfigureAwait(false))
            throw new IOException("History was saved, but the recovery recording could not be removed.");

        return record;
    }

    private async Task<string> CopyHistoryAudioAsync(string sourcePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_historyAudioPath);
        var fileName = $"{Guid.NewGuid():N}.wav";
        var destination = Path.Combine(_historyAudioPath, fileName);
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return fileName;
        }
        catch
        {
            DeleteHistoryAudio(fileName);
            throw;
        }
    }

    private void DeleteHistoryAudio(string fileName)
    {
        try
        {
            File.Delete(Path.Combine(_historyAudioPath, fileName));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
