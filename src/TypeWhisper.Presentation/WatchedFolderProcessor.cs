using System.Text;
using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>Explicitly authorized automatic transcription into a separate export folder.</summary>
public sealed record WatchedFolderSettings(string Input, string Output, string Format = "txt", bool StartWithApp = false);

/// <summary>A durable source revision. Interrupted requests require explicit retry.</summary>
public sealed record WatchedFile(Guid Id, string Path, long Length, long ModifiedTicks, string Status,
    string? Message = null, FileTranscriptionRecoveryResult? Result = null, string? ExportPath = null);

/// <summary>Serial folder processing, independent of the visible page. Call mutations on the owning thread.</summary>
public sealed class WatchedFolderProcessor
{
    private sealed record State(int Version, WatchedFolderSettings? Settings, List<WatchedFile> Files);
    private readonly string _statePath;
    private readonly Dictionary<string, (long Length, long Ticks)> _observed = new(StringComparer.OrdinalIgnoreCase);
    private List<WatchedFile> _files = [];
    private CancellationTokenSource? _operation;
    private Task _completion = Task.CompletedTask;
    private bool _shutdown;
    private bool _busy;
    private bool _loadFailed;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Loads saved settings and progress without starting processing.</summary>
    public WatchedFolderProcessor(string statePath)
    {
        _statePath = statePath;
        try
        {
            if (!File.Exists(statePath)) return;
            if (new FileInfo(statePath).Length > 32 * 1024 * 1024) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath), Json);
            if (saved is null || saved.Version != 1 || saved.Files is null || saved.Files.Count > 2000
                || saved.Files.Select(f => f?.Id).Distinct().Count() != saved.Files.Count
                || saved.Settings is { } settings && (!Path.IsPathFullyQualified(settings.Input) || !Path.IsPathFullyQualified(settings.Output)
                    || settings.Format is not ("txt" or "srt" or "vtt") || string.Equals(settings.Input, settings.Output, StringComparison.OrdinalIgnoreCase))
                || saved.Files.Any(f => f is null || f.Id == Guid.Empty || string.IsNullOrEmpty(f.Path) || !Path.IsPathFullyQualified(f.Path)
                    || f.Length < 0 || f.ModifiedTicks < 0 || f.ModifiedTicks > DateTime.MaxValue.Ticks
                    || f.ExportPath is not null && (!Path.IsPathFullyQualified(f.ExportPath) || string.Equals(f.ExportPath, f.Path, StringComparison.OrdinalIgnoreCase))
                    || f.Status is "Completed" or "Export pending" && f.Result is null
                    || f.Result is { } result && (result.Text is null || result.Text.Length > 2_000_000 || result.Segments is null
                        || result.Segments.Count > 100_000 || !double.IsFinite(result.Duration) || result.Duration < 0)
                    || f.Status is not ("Processing" or "Failed" or "Export pending" or "Completed" or "Queued")))
                throw new InvalidDataException();
            Settings = saved.Settings;
            _files = saved.Files.Select(f => f.Status == "Processing"
                ? f with { Status = "Failed", Message = "Interrupted. Retry explicitly; the provider may already have processed this file." } : f).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _loadFailed = true; Error = "Saved folder processing data could not be read. Existing data was preserved."; }
    }

    /// <summary>The explicitly saved folder configuration.</summary>
    public WatchedFolderSettings? Settings { get; private set; }
    /// <summary>Durable source revisions and export results.</summary>
    public IReadOnlyList<WatchedFile> Files => _files.AsReadOnly();
    /// <summary>Whether automatic polling is enabled.</summary>
    public bool Watching { get; private set; }
    /// <summary>Whether one scan, provider call or export is still draining.</summary>
    public bool Busy => _busy;
    /// <summary>Current user-visible processing status.</summary>
    public string Status { get; private set; } = "Not watching";
    /// <summary>A persistence failure that blocks automatic work.</summary>
    public string? Error { get; private set; }
    /// <summary>Raised on the owning context when visible state changes.</summary>
    public event Action? Changed;

    /// <summary>Validates and saves configuration while paused.</summary>
    public bool Configure(WatchedFolderSettings settings)
    {
        if (Busy || Watching || _shutdown || Error is not null) return false;
        try
        {
            var input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Input));
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(settings.Output) ? Path.Combine(input, "Transcripts") : settings.Output));
            if (!Directory.Exists(input)) throw new IOException("Choose an existing watch folder.");
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a separate output folder.");
            if (settings.Format is not ("txt" or "srt" or "vtt")) throw new IOException("Choose TXT, SRT or VTT.");
            Directory.CreateDirectory(output);
            var previous = Settings;
            Settings = settings with { Input = input, Output = output };
            var previousFiles = _files;
            if (previous?.Output != output || previous?.Format != settings.Format)
                _files = _files.Select(f => f.Status != "Completed" && f.Result is not null ? f with { ExportPath = null } : f).ToList();
            if (!Save()) { Settings = previous; _files = previousFiles; return false; }
            _observed.Clear();
            Status = "Folders saved. Start watching to process supported files in this folder.";
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Status = ex.Message; Changed?.Invoke(); return false; }
    }

    /// <summary>Retries a failed progress write without replacing an unreadable checkpoint.</summary>
    public bool RetrySavingProgress()
    {
        if (_loadFailed || Busy || _shutdown || Error is null) return false;
        Error = null;
        var saved = Save();
        Changed?.Invoke();
        return saved;
    }

    /// <summary>Explicitly enables processing of existing and new supported files.</summary>
    public void Start()
    {
        if (_shutdown || Settings is null || Error is not null || Busy) return;
        Watching = true;
        Status = "Waiting for stable audio or video files";
        Changed?.Invoke();
    }

    /// <summary>Pauses discovery and cancels the active request without deleting media.</summary>
    public void Stop()
    {
        Watching = false;
        _operation?.Cancel();
        Status = Busy ? "Stopping after the current operation drains…" : "Paused";
        Changed?.Invoke();
    }

    /// <summary>Explicitly retries failures; retained transcripts need only export.</summary>
    public bool RetryFailures()
    {
        if (Busy || _shutdown || Error is not null) return false;
        _files = _files.Select(f => f.Status == "Failed" ? f with
        { Status = f.Result is null ? "Queued" : "Export pending", Message = null } : f).ToList();
        if (!Save()) return false;
        Start();
        return true;
    }

    /// <summary>Scans once and processes at most one stable file when the provider is idle.</summary>
    public Task PollAsync(bool providerReady, Func<string, Action<string>, CancellationToken, Task<FileTranscriptionOutput>> process)
    {
        if (!Watching || Busy || _shutdown || Settings is null || Error is not null) return Task.CompletedTask;
        _busy = true;
        var cancellation = _operation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion.Task;
        _ = ExecuteAsync(providerReady, process, cancellation, completion);
        return completion.Task;
    }

    private async Task ExecuteAsync(bool providerReady, Func<string, Action<string>, CancellationToken, Task<FileTranscriptionOutput>> process,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        WatchedFile? active = null;
        var ct = cancellation.Token;
        var settings = Settings!;
        try
        {
            // Export a retained result before considering another provider request.
            active = _files.FirstOrDefault(f => f.Status == "Export pending");
            if (active is null)
            {
                if (!providerReady) { Status = "Waiting for a ready, idle dictation model"; return; }
                var sources = await Task.Run(() => Directory.EnumerateFiles(settings.Input)
                    .Where(p => FileTranscriptionQueue.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .Take(2001).Select(p => new FileInfo(p)).Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray(), ct);
                ct.ThrowIfCancellationRequested();
                if (sources.Length > 2000) throw new IOException("This folder has more than 2,000 media files. Choose a smaller folder.");
                var present = sources.Select(s => s.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var removed in _observed.Keys.Where(p => !present.Contains(p)).ToArray()) _observed.Remove(removed);
                foreach (var source in sources.OrderBy(s => s.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    if ((File.GetAttributes(source.FullName) & FileAttributes.ReparsePoint) != 0) continue;
                    var revision = (source.Length, source.Ticks);
                    var stable = _observed.TryGetValue(source.FullName, out var last) && last == revision;
                    _observed[source.FullName] = revision;
                    if (!stable || source.Length == 0) continue;
                    var existing = _files.LastOrDefault(f => string.Equals(f.Path, source.FullName, StringComparison.OrdinalIgnoreCase)
                        && f.Length == source.Length && f.ModifiedTicks == source.Ticks);
                    if (existing is not null && existing.Status != "Queued") continue;
                    if (existing is null && _files.Count >= 2000) throw new IOException("The folder log has reached 2,000 files. Pause watching and export your results before continuing.");
                    // Hold a read lease through decoding/inference so writers and replacements cannot change this revision.
                    FileStream lease;
                    try { lease = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read); }
                    catch (IOException) { continue; }
                    using (lease)
                    {
                        var current = new FileInfo(source.FullName);
                        if (lease.Length != source.Length || current.LastWriteTimeUtc.Ticks != source.Ticks) continue;
                        active = (existing ?? new WatchedFile(Guid.NewGuid(), source.FullName, source.Length, source.Ticks, "Queued"))
                            with { Status = "Processing", Message = null };
                        Put(active);
                        if (!Save()) { Put(active with { Status = "Queued", Message = "Waiting for progress storage to recover." }); return; }
                        Status = "Transcribing " + Path.GetFileName(active.Path);
                        Changed?.Invoke();
                        var output = await process(active.Path, stage => { if (!ct.IsCancellationRequested) { Status = stage; Changed?.Invoke(); } }, ct);
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(output.Text)) throw new IOException("No speech was recognized.");
                        active = active with { Status = "Export pending", Result = FileTranscriptionRecoveryResult.FromOutput(output) };
                        Put(active);
                        if (!Save()) return;
                    }
                    break;
                }
            }
            if (active?.Status == "Export pending")
            {
                ct.ThrowIfCancellationRequested();
                var job = new FileTranscriptionJob(active.Path) { Status = FileTranscriptionStatus.Ready, Result = active.Result!.ToOutput() };
                var text = FileTranscriptionQueue.Export(job, settings.Format);
                var destination = active.ExportPath ?? TranscriptFileExport.Destination(settings.Output, active.Path, settings.Format, active.Id);
                // Persist the exact destination before publication. A restart may verify this file but never overwrite it.
                active = active with { ExportPath = destination }; Put(active);
                if (!Save()) return;
                await TranscriptFileExport.PublishAsync(destination, text, ct, verifyExisting: true);
                active = active with { Status = "Completed", Message = job.Result.Warning };
                Put(active);
                if (!Save()) return;
                Status = "Exported " + Path.GetFileName(active.Path);
            }
            else Status = "Watching · waiting for new or finished files";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (active is not null && active.Status == "Processing")
            { Put(active with { Status = "Failed", Message = "Stopped. Retry explicitly to transcribe again." }); Save(); }
            Status = "Paused";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (active is not null)
            { Put(active with { Status = "Failed", Message = ex is IOException or InvalidOperationException ? ex.Message : "Processing failed. Retry when the provider and folders are available." }); Save(); }
            Status = active is null ? "Folder unavailable. Check the paths and access; watching will retry." : "A file needs attention. Other files can continue.";
        }
        finally
        {
            _operation = null; _busy = false; cancellation.Dispose();
            if (Error is not null) Watching = false;
            completion.TrySetResult();
            Changed?.Invoke();
        }
    }

    /// <summary>Permanently stops discovery and drains the active operation.</summary>
    public async Task ShutdownAsync()
    {
        _shutdown = true;
        Stop();
        await _completion;
    }

    private void Put(WatchedFile file)
    {
        var index = _files.FindIndex(f => f.Id == file.Id);
        if (index < 0) _files.Add(file); else _files[index] = file;
    }

    private bool Save()
    {
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(new State(1, Settings, _files), Json);
            if (serialized.Length > 32 * 1024 * 1024) throw new IOException("Saved folder progress is too large.");
            File.WriteAllBytes(temporary, serialized);
            File.Move(temporary, _statePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = "Folder progress could not be saved. Watching stopped to prevent duplicate processing."; Watching = false; return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

}
