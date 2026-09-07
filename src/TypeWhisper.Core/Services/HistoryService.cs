using System.Text;
using System.Text.Json;
using System.Globalization;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides history service behavior.
/// </summary>
public sealed class HistoryService : IHistoryAudioService
{
    private readonly string _filePath;
    /// <summary>Propagates read/format failures instead of treating them as empty history. Missing files remain empty.</summary>
    public bool ThrowOnLoadFailure { get; init; }
    private readonly string? _audioDirectory;
    private readonly HistoryAudioStore? _audioStore;
    private string? _legacyAudioError;
    private readonly object _gate = new();
    private List<TranscriptionRecord> _cache = [];
    private bool _cacheLoaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private int _totalRecords;
    private int _totalWords;
    private double _totalDuration;
    private List<string> _distinctApps = [];

    /// <summary>
    /// Gets the persisted transcription history records.
    /// </summary>
    public IReadOnlyList<TranscriptionRecord> Records
    {
        get
        {
            EnsureCacheLoaded();
            lock (_gate)
            {
                return _cache.ToList();
            }
        }
    }

    /// <summary>
    /// Raised when records changes.
    /// </summary>
    public event Action? RecordsChanged;

    /// <summary>
    /// Gets the number of persisted transcription history records.
    /// </summary>
    public int TotalRecords => _cacheLoaded ? _totalRecords : Records.Count;
    /// <summary>
    /// Performs total words.
    /// </summary>
    public int TotalWords => _cacheLoaded ? _totalWords : Records.Sum(r => r.WordCount);
    /// <summary>
    /// Performs total duration.
    /// </summary>
    public double TotalDuration => _cacheLoaded ? _totalDuration : Records.Sum(r => r.DurationSeconds);

    /// <summary>
    /// Initializes a new instance of the HistoryService class.
    /// </summary>
    public HistoryService(string filePath, string? audioDirectory = null, HistoryAudioStore? audioStore = null)
    {
        _filePath = filePath;
        _audioDirectory = audioDirectory;
        _audioStore = audioStore;
    }

    /// <summary>
    /// Ensures loaded asynchronously..
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_cacheLoaded) return;

        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheLoaded) return;
            var records = await Task.Run(LoadFromDisk).ConfigureAwait(false);
            lock (_gate)
            {
                _cache = records;
                RebuildStats();
                _cacheLoaded = true;
                _audioStore?.Reconcile(AudioReferences());
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Returns distinct apps.
    /// </summary>
    public IReadOnlyList<string> GetDistinctApps()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return _distinctApps.ToList();
        }
    }

    /// <summary>
    /// Adds record.
    /// </summary>
    public void AddRecord(TranscriptionRecord record)
    {
        _ = TryAddRecord(record);
    }

    /// <inheritdoc />
    public bool TryAddRecord(TranscriptionRecord record)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        lock (_gate)
        {
            if (_cache.Any(existing => string.Equals(existing.Id, record.Id, StringComparison.Ordinal)))
                return false;

            var updated = _cache.ToList();
            updated.Insert(0, record);
            if (!SaveToDisk(updated))
                return false;

            _cache = updated;
            RebuildStats();
        }

        RaiseRecordsChanged();
        return true;
    }

    /// <inheritdoc />
    public string? AudioCleanupError => _audioStore?.CleanupError ?? _legacyAudioError;

    /// <inheritdoc />
    public string? RetryAudioCleanup()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        lock (_gate) return _audioStore?.Reconcile(AudioReferences()) ?? _legacyAudioError;
    }

    /// <inheritdoc />
    public string? ResolveAudioPath(string? fileName) => _audioStore?.Resolve(fileName);

    private string[] AudioReferences() => _cache.Select(record => record.AudioFileName)
        .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public HistoryAudioSaveResult TryAddRecordWithAudio(TranscriptionRecord record, float[] samples, int sampleRate,
        Func<bool> maySaveAudio, CancellationToken cancellationToken = default, Func<bool>? maySaveHistory = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(maySaveAudio);
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        var actual = record with { AudioFileName = null };
        string? warning = null;
        var saved = false;
        var suppressed = false;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maySaveHistory?.Invoke() == false) return new(actual, false, null) { Suppressed = true };
            if (_cache.Any(existing => existing.Id == record.Id)) return new(actual, false, null);
            try
            {
                if (maySaveAudio())
                {
                    try
                    {
                        if (_audioStore is null) warning = "Audio storage is unavailable. The text can still be saved.";
                        else actual = actual with { AudioFileName = _audioStore.Prepare(samples, sampleRate, cancellationToken) };
                    }
                    catch (Exception ex) when (HistoryAudioStore.Recoverable(ex))
                    { warning = "Audio could not be saved. The text can still be saved to History."; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Last synchronous permission boundary before committing the History reference.
                if (!maySaveAudio()) actual = actual with { AudioFileName = null };
                cancellationToken.ThrowIfCancellationRequested();
                if (maySaveHistory?.Invoke() == false)
                { suppressed = true; actual = actual with { AudioFileName = null }; warning = null; }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var updated = _cache.ToList();
                    updated.Insert(0, actual);
                    saved = SaveToDisk(updated);
                    if (saved) { _cache = updated; RebuildStats(); }
                }
            }
            finally
            {
                var cleanup = _audioStore?.Reconcile(AudioReferences());
                if (cleanup is not null) warning = warning is null ? cleanup : warning + " " + cleanup;
            }
        }
        if (saved) RaiseRecordsChanged();
        else actual = actual with { AudioFileName = null };
        return new(actual, saved, warning) { Suppressed = suppressed };
    }

    /// <summary>
    /// Updates record.
    /// </summary>
    public void UpdateRecord(string id, string finalText)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        var changed = false;
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
                return;

            var updated = _cache.ToList();
            updated[idx] = updated[idx] with { FinalText = finalText };
            if (!SaveToDisk(updated))
                return;

            _cache = updated;
            RebuildStats();
            changed = true;
        }

        if (changed)
            RaiseRecordsChanged();
    }

    /// <inheritdoc />
    public bool TryReplaceRecord(TranscriptionRecord record)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        lock (_gate)
        {
            var index = _cache.FindIndex(existing =>
                string.Equals(existing.Id, record.Id, StringComparison.Ordinal));
            if (index < 0)
                return false;

            var previousAudio = _cache[index].AudioFileName;
            var updated = _cache.ToList();
            updated[index] = record;
            var removeAudio = !string.IsNullOrWhiteSpace(previousAudio) &&
                !updated.Any(item => string.Equals(item.AudioFileName, previousAudio, StringComparison.OrdinalIgnoreCase));
            if (removeAudio && _audioStore?.MarkDeletion([previousAudio]) == false) return false;
            if (!SaveToDisk(updated))
                return false;

            _cache = updated;
            RebuildStats();
            if (removeAudio)
            {
                if (_audioStore is not null) _audioStore.Reconcile(AudioReferences());
                else DeleteAudioFile(previousAudio);
            }
        }

        RaiseRecordsChanged();
        return true;
    }

    /// <summary>
    /// Deletes record.
    /// </summary>
    public void DeleteRecord(string id) => TryDeleteRecords([id]);

    /// <summary>Removes current entries and journals only their unshared owned audio for cleanup.</summary>
    public void ClearAll() => RemoveMatching(_ => true);

    /// <inheritdoc />
    public bool TryDeleteRecords(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return selected.Count == 0 || RemoveMatching(record => selected.Contains(record.Id));
    }

    private bool RemoveMatching(Func<TranscriptionRecord, bool> remove)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        lock (_gate)
        {
            var removed = _cache.Where(remove).ToArray();
            if (removed.Length == 0) return true;
            var remaining = _cache.Where(record => !remove(record)).ToList();
            var references = remaining.Select(record => record.AudioFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var audioFiles = removed.Select(record => record.AudioFileName).Where(name => !string.IsNullOrWhiteSpace(name) && !references.Contains(name)).ToArray();
            if (audioFiles.Length > 0 && _audioStore?.MarkDeletion(audioFiles) == false) return false;
            if (!SaveToDisk(remaining)) return false;
            _cache = remaining;
            RebuildStats();
            if (_audioStore is not null) _audioStore.Reconcile(AudioReferences());
            else DeleteAudioFiles(audioFiles);
        }
        RaiseRecordsChanged();
        return true;
    }

    /// <summary>
    /// Performs search.
    /// </summary>
    public IReadOnlyList<TranscriptionRecord> Search(string query)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query)) return _cache.ToList();

            return _cache.Where(r =>
                r.DisplayText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.RawText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }
    }

    /// <summary>
    /// Performs purge old records.
    /// </summary>
    public void PurgeOldRecords(TimeSpan? retention)
    {
        if (retention is null) return;
        var cutoff = DateTime.UtcNow - retention.Value;
        RemoveMatching(record => record.CreatedAt < cutoff);
    }

    /// <summary>
    /// Exports to text.
    /// </summary>
    public string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine(l.Header);
        sb.AppendLine($"{l.Exported}: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"{l.Entries}: {records.Count}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"[{r.Timestamp:dd.MM.yyyy HH:mm}] {r.AppProcessName ?? "–"} ({r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s)");
            sb.AppendLine(r.DisplayText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exports to csv.
    /// </summary>
    public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"{l.Timestamp},{l.App},{l.Text},{l.Duration},{l.Words},{l.Language}");

        foreach (var r in records)
        {
            var text = "\"" + r.DisplayText.Replace("\"", "\"\"") + "\"";
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.AppProcessName ?? ""},{text},{r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)},{r.WordCount},{r.Language ?? ""}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exports to markdown.
    /// </summary>
    public string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"# {l.Header}");
        sb.AppendLine();
        sb.AppendLine($"- **{l.Exported}:** {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"- **{l.Entries}:** {records.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"## {r.Timestamp:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.AppProcessName))
                sb.AppendLine($"- **{l.App}:** {r.AppProcessName}");
            sb.AppendLine($"- **{l.Duration}:** {r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
            if (!string.IsNullOrEmpty(r.Language))
                sb.AppendLine($"- **{l.Language}:** {r.Language}");
            sb.AppendLine();
            sb.AppendLine(r.DisplayText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exports the current data as JSON.
    /// </summary>
    public string ExportToJson(IReadOnlyList<TranscriptionRecord> records)
    {
        var data = records.Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp.ToString("o"),
            text = r.DisplayText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            duration_seconds = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            workflow_id = r.WorkflowId,
            status = r.Status.ToString(),
            workflow_failure = r.WorkflowFailureMessage,
            recovery_audio_file = r.RecoveryAudioFileName,
            transcription_task = r.TranscriptionTaskUsed,
            used_transcription_fallback = r.UsedTranscriptionFallback,
            words = r.WordCount
        });

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <inheritdoc />
    public bool TryReplaceAll(IReadOnlyList<TranscriptionRecord> records)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        lock (_gate)
        {
            var replacement = records
                .OrderByDescending(record => record.Timestamp)
                .ToList();
            if (!SaveToDisk(replacement))
                return false;

            _cache = replacement;
            RebuildStats();
        }

        RaiseRecordsChanged();
        return true;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        _loadLock.Wait();
        try
        {
            if (_cacheLoaded) return;
            var records = LoadFromDisk();
            lock (_gate)
            {
                _cache = records;
                RebuildStats();
                _cacheLoaded = true;
                _audioStore?.Reconcile(AudioReferences());
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private List<TranscriptionRecord> LoadFromDisk()
    {
        try
        {
            if (!ThrowOnLoadFailure && _audioStore is null && !File.Exists(_filePath)) return [];

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<TranscriptionRecord>>(json) ?? [];
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch when (!ThrowOnLoadFailure && _audioStore is null)
        {
            return [];
        }
    }

    private bool SaveToDisk(IReadOnlyList<TranscriptionRecord> records)
    {
        string? temporaryPath = null;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            temporaryPath = Path.Combine(
                dir ?? Directory.GetCurrentDirectory(),
                $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private void RebuildStats()
    {
        _totalRecords = _cache.Count;
        _totalWords = _cache.Sum(r => r.WordCount);
        _totalDuration = _cache.Sum(r => r.DurationSeconds);
        RebuildDistinctApps();
    }

    private void RaiseRecordsChanged()
    {
        if (RecordsChanged is not { } handlers)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); } catch { }
        }
    }

    private void RebuildDistinctApps()
    {
        _distinctApps = _cache
            .Select(r => r.AppProcessName)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList()!;
    }

    private void DeleteAudioFile(string? audioFileName)
    {
        if (string.IsNullOrEmpty(audioFileName) || string.IsNullOrEmpty(_audioDirectory)) return;
        try
        {
            if (audioFileName != Path.GetFileName(audioFileName) || audioFileName.IndexOfAny(['/', '\\', ':']) >= 0 || audioFileName is "." or "..")
                throw new IOException("Unsafe legacy audio filename.");
            var directory = Path.GetFullPath(_audioDirectory);
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked audio directory.");
            var path = Path.Combine(directory, audioFileName);
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked audio file.");
                File.Delete(path);
            }
        }
        catch (Exception ex) when (HistoryAudioStore.Recoverable(ex))
        { _legacyAudioError = "Some legacy audio could not be removed. The text History has already been updated."; }
    }

    private void DeleteAudioFiles(IEnumerable<string?> audioFileNames)
    {
        foreach (var audioFileName in audioFileNames)
            DeleteAudioFile(audioFileName);
    }
}
