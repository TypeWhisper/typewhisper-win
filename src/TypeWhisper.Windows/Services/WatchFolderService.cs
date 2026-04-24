using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Monitors a folder for supported audio/video files and runs transcription automation jobs.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _processedFingerprintsPath;
    private readonly string _historyPath;
    private readonly ConcurrentQueue<string> _pendingFiles = [];
    private readonly ConcurrentDictionary<string, byte> _queuedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateGate = new();
    private readonly object _persistenceGate = new();
    private readonly HashSet<string> _processedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchFolderHistoryItem> _history = [];

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private Task? _rescanTask;
    private Func<WatchFolderTranscriptionRequest, CancellationToken, Task<WatchFolderTranscriptionResult>>? _transcribeHandler;
    private WatchFolderOptions? _options;
    private bool _disposed;

    public WatchFolderService()
        : this(TypeWhisperEnvironment.DataPath)
    {
    }

    internal WatchFolderService(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _processedFingerprintsPath = Path.Combine(dataPath, "watch-folder-processed.json");
        _historyPath = Path.Combine(dataPath, "watch-folder-history.json");
        LoadProcessedFingerprints();
        LoadHistory();
    }

    public string? WatchPath { get; private set; }
    public string? CurrentlyProcessing { get; private set; }
    public bool IsRunning => _watcher is not null;
    public IReadOnlyList<WatchFolderHistoryItem> History
    {
        get
        {
            lock (_stateGate)
            {
                return _history.ToList();
            }
        }
    }

    public event EventHandler? StateChanged;
    public event EventHandler<WatchFolderHistoryItem>? FileProcessed;

    public void Start(
        WatchFolderOptions options,
        Func<WatchFolderTranscriptionRequest, CancellationToken, Task<WatchFolderTranscriptionResult>> transcribeHandler)
    {
        ThrowIfDisposed();
        Stop();

        if (string.IsNullOrWhiteSpace(options.WatchPath))
            throw new ArgumentException("Watch folder path is required.", nameof(options));

        Directory.CreateDirectory(options.WatchPath);
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
            Directory.CreateDirectory(options.OutputPath);

        _options = options;
        _transcribeHandler = transcribeHandler;
        _cts = new CancellationTokenSource();
        WatchPath = options.WatchPath;

        _watcher = new FileSystemWatcher(options.WatchPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileCreated;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        ScanFolder(options.WatchPath);
        _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        _rescanTask = Task.Run(() => RescanLoopAsync(options.WatchPath, _cts.Token));
        OnStateChanged();
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _processingTask = null;
        _rescanTask = null;
        _transcribeHandler = null;
        _options = null;
        WatchPath = null;
        CurrentlyProcessing = null;

        while (_pendingFiles.TryDequeue(out _)) { }
        _queuedFiles.Clear();
        _activeFiles.Clear();
        lock (_persistenceGate)
        {
            _failedFingerprints.Clear();
        }

        OnStateChanged();
    }

    public void ClearHistory()
    {
        lock (_stateGate)
        {
            _history.Clear();
        }

        SaveHistory();
        OnStateChanged();
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        TryScanEventFolder(e.FullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        TryScanEventFolder(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        TryScanEventFolder(e.FullPath);
    }

    private void TryScanEventFolder(string filePath)
    {
        try
        {
            ScanEventFolder(filePath);
        }
        catch (Exception ex) when (IsExpectedFolderScanException(ex))
        {
            Debug.WriteLine($"WatchFolder event scan failed: {ex}");
        }
    }

    private void ScanEventFolder(string filePath)
    {
        var folderPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(folderPath))
            ScanFolder(folderPath);
    }

    private void ScanFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(folderPath).Where(AudioFileService.IsSupported).OrderBy(Path.GetFileName))
                EnqueueFile(filePath);
        }
        catch (Exception ex) when (IsExpectedFolderScanException(ex))
        {
            Debug.WriteLine($"WatchFolder scan failed: {ex}");
        }
    }

    private void EnqueueFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (_activeFiles.ContainsKey(fullPath))
            return;

        var fingerprint = CreateFingerprint(fullPath);
        if (fingerprint is null || IsKnownFingerprint(fingerprint))
            return;

        if (!_queuedFiles.TryAdd(fullPath, 0))
            return;

        _pendingFiles.Enqueue(fullPath);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_pendingFiles.TryDequeue(out var filePath))
            {
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            _queuedFiles.TryRemove(filePath, out _);
            try
            {
                await ProcessFileAsync(filePath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RescanLoopAsync(string folderPath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                ScanFolder(folderPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsExpectedFolderScanException(ex))
            {
                Debug.WriteLine($"WatchFolder rescan failed: {ex}");
            }
        }
    }

    private async Task ProcessFileAsync(string filePath, CancellationToken ct)
    {
        filePath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(filePath);
        string? fingerprint = null;
        _activeFiles.TryAdd(filePath, 0);
        CurrentlyProcessing = fileName;
        OnStateChanged();

        try
        {
            await WaitForFileReadyAsync(filePath, ct);
            fingerprint = CreateFingerprint(filePath);
            if (fingerprint is null || IsKnownFingerprint(fingerprint))
                return;

            var options = _options ?? throw new InvalidOperationException("Watch folder options are not available.");
            var transcribeHandler = _transcribeHandler ?? throw new InvalidOperationException("Watch folder transcriber is not available.");
            var result = await transcribeHandler(new WatchFolderTranscriptionRequest(filePath), ct);

            var outputFolder = string.IsNullOrWhiteSpace(options.OutputPath)
                ? options.WatchPath
                : options.OutputPath!;
            Directory.CreateDirectory(outputFolder);

            var artifact = WatchFolderExportBuilder.Build(
                options.OutputFormat,
                result,
                fileName,
                ResolveEngineName(result),
                DateTime.Now);
            var outputPath = Path.Combine(
                outputFolder,
                Path.GetFileNameWithoutExtension(filePath) + "." + artifact.FileExtension);

            await File.WriteAllTextAsync(outputPath, artifact.Content, ct);

            if (options.DeleteSource)
                File.Delete(filePath);

            AddProcessedFingerprint(fingerprint);
            AddHistory(new WatchFolderHistoryItem
            {
                Id = Guid.NewGuid().ToString(),
                FileName = fileName,
                ProcessedAtUtc = DateTime.UtcNow,
                OutputPath = outputPath,
                Success = true
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException) when (!File.Exists(filePath))
        {
            Debug.WriteLine($"WatchFolder skipped deleted file: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WatchFolder transcription failed: {ex.Message}");
            if (fingerprint is not null)
                AddFailedFingerprint(fingerprint);

            AddHistory(new WatchFolderHistoryItem
            {
                Id = Guid.NewGuid().ToString(),
                FileName = fileName,
                ProcessedAtUtc = DateTime.UtcNow,
                OutputPath = "",
                Success = false,
                ErrorMessage = ex.Message
            });
        }
        finally
        {
            _activeFiles.TryRemove(filePath, out _);
            CurrentlyProcessing = null;
            OnStateChanged();
        }
    }

    private static string ResolveEngineName(WatchFolderTranscriptionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.EngineId) && !string.IsNullOrWhiteSpace(result.ModelId))
            return $"{result.EngineId} / {result.ModelId}";

        return result.EngineId ?? result.ModelId ?? "Default";
    }

    private bool IsKnownFingerprint(string fingerprint)
    {
        lock (_persistenceGate)
        {
            return _processedFingerprints.Contains(fingerprint)
                || _failedFingerprints.Contains(fingerprint);
        }
    }

    private void AddProcessedFingerprint(string fingerprint)
    {
        lock (_persistenceGate)
        {
            _failedFingerprints.Remove(fingerprint);
            _processedFingerprints.Add(fingerprint);
            SaveProcessedFingerprintsCore();
        }
    }

    private void AddFailedFingerprint(string fingerprint)
    {
        lock (_persistenceGate)
        {
            _failedFingerprints.Add(fingerprint);
        }
    }

    private void AddHistory(WatchFolderHistoryItem item)
    {
        lock (_stateGate)
        {
            _history.Insert(0, item);
            if (_history.Count > 100)
                _history.RemoveRange(100, _history.Count - 100);
        }

        SaveHistory();
        FileProcessed?.Invoke(this, item);
        OnStateChanged();
    }

    private static async Task WaitForFileReadyAsync(string path, CancellationToken ct)
    {
        long? previousLength = null;
        DateTime? previousWrite = null;
        var stableReads = 0;

        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
                throw new FileNotFoundException("Watch folder file no longer exists.", path);

            try
            {
                var info = new FileInfo(path);
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var currentLength = info.Length;
                var currentWrite = info.LastWriteTimeUtc;

                if (previousLength == currentLength && previousWrite == currentWrite)
                    stableReads++;
                else
                    stableReads = 0;

                if (stableReads >= 1)
                    return;

                previousLength = currentLength;
                previousWrite = currentWrite;
            }
            catch (IOException)
            {
                stableReads = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableReads = 0;
            }

            await Task.Delay(250, ct);
        }

        throw new IOException("Watch folder file is still being written.");
    }

    private static string? CreateFingerprint(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    private void LoadProcessedFingerprints()
    {
        try
        {
            if (!File.Exists(_processedFingerprintsPath))
                return;

            var json = File.ReadAllText(_processedFingerprintsPath);
            var loaded = JsonSerializer.Deserialize<HashSet<string>>(json, JsonOptions);
            if (loaded is null)
                return;

            foreach (var fingerprint in loaded)
                _processedFingerprints.Add(fingerprint);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to load watch folder fingerprints: {ex}");
        }
    }

    private void SaveProcessedFingerprintsCore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_processedFingerprintsPath)!);
            var json = JsonSerializer.Serialize(_processedFingerprints, JsonOptions);
            File.WriteAllText(_processedFingerprintsPath, json);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to save watch folder fingerprints: {ex}");
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath))
                return;

            var json = File.ReadAllText(_historyPath);
            var loaded = JsonSerializer.Deserialize<List<WatchFolderHistoryItem>>(json, JsonOptions);
            if (loaded is null)
                return;

            _history.Clear();
            _history.AddRange(loaded.Take(100));
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to load watch folder history: {ex}");
        }
    }

    private void SaveHistory()
    {
        try
        {
            List<WatchFolderHistoryItem> snapshot;
            lock (_stateGate)
            {
                snapshot = _history.Take(100).ToList();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to save watch folder history: {ex}");
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsExpectedFolderScanException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException;

    private static bool IsExpectedPersistenceException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or JsonException
            or NotSupportedException;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
