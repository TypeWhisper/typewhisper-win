using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Observable phases of an explicitly started file request.</summary>
public enum FileTranscriptionStatus
{
    /// <summary>Waiting for an explicit run.</summary>
    Queued,
    /// <summary>Reading or transcribing the selected media.</summary>
    Processing,
    /// <summary>A real result is available.</summary>
    Ready,
    /// <summary>Canceled without accepting a late result.</summary>
    Canceled,
    /// <summary>A retryable operation failure.</summary>
    Failed
}

/// <summary>Real text and optional provider timing, with the configuration actually used.</summary>
public sealed record FileTranscriptionOutput(string Text, string Provider, string Model, double Duration,
    IReadOnlyList<TranscriptionSegment> Segments, string? Warning = null)
{
    /// <summary>Provider/model label captured when processing began, independent of later selection changes.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Snippet IDs actually expanded, pending usage recording when the queue accepts this result.</summary>
    public IReadOnlyList<string> AppliedSnippetIds { get; init; } = [];
    /// <summary>Immutable history data prepared only when saving was allowed at request start. No write occurs until acceptance.</summary>
    public TranscriptionRecord? PendingHistory { get; init; }
}

/// <summary>One file selected by the user; the queue never deletes source media.</summary>
public sealed class FileTranscriptionJob(string path)
{
    /// <summary>Stable queue identity.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Original absolute media path.</summary>
    public string Path { get; } = path;
    /// <summary>Display filename.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
    /// <summary>Current operation phase.</summary>
    public FileTranscriptionStatus Status { get; internal set; }
    /// <summary>Provider or decoder status, without invented progress.</summary>
    public string Stage { get; internal set; } = "Queued";
    /// <summary>Accepted result, available only after success.</summary>
    public FileTranscriptionOutput? Result { get; internal set; }
}

/// <summary>Serial file execution with explicit retry and cancellation that rejects late provider results.</summary>
/// <remarks>Call mutations on the owning UI thread. This queue is not yet a durable recovery store.</remarks>
public sealed class FileTranscriptionQueue
{
    /// <summary>File extensions offered to the native media decoder.</summary>
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[]
        { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".mp4", ".mov", ".webm", ".aac", ".wma", ".mkv", ".avi" });
    private readonly List<FileTranscriptionJob> _jobs = [];
    private CancellationTokenSource? _run;
    private Task _runCompletion = Task.CompletedTask;
    private bool _shutdown;
    /// <summary>Current queue order.</summary>
    public IReadOnlyList<FileTranscriptionJob> Jobs => _jobs.AsReadOnly();
    /// <summary>True until the active request has actually drained.</summary>
    public bool Running => _run is not null;
    /// <summary>Completes after the current or most recent run and its decoder have drained.</summary>
    public Task RunCompletion => _runCompletion;
    /// <summary>True after shutdown permanently prevents new queue work.</summary>
    public bool IsShutdown => _shutdown;
    /// <summary>Notifies the owning surface after a state change.</summary>
    public event Action? Changed;

    /// <summary>Adds a source path, returning a validation error when rejected.</summary>
    public string? Add(string path)
    {
        if (_shutdown) return "The file queue has shut down.";
        if (Running) return "Wait for the current run to finish before adding files.";
        if (string.IsNullOrWhiteSpace(path) || !Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return "Choose a supported audio or video file.";
        try { path = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "The file path is invalid."; }
        if (_jobs.Any(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase))) return "This file is already in the queue.";
        if (_jobs.Count >= 20) return "The queue supports up to 20 files at a time.";
        _jobs.Add(new(path)); Changed?.Invoke(); return null;
    }

    /// <summary>Processes queued files serially, preserving completed results on failure or cancellation.</summary>
    /// <param name="process">Decodes and formats one file without writing history or snippet usage.</param>
    /// <param name="onAccepted">Optional synchronous persistence commit, called once per accepted result. Its returned warning is shown with the result; failures never discard accepted text.</param>
    public Task RunAsync(Func<string, Action<string>, CancellationToken, Task<FileTranscriptionOutput>> process,
        Func<FileTranscriptionOutput, string?>? onAccepted = null)
    {
        if (_shutdown || Running || !_jobs.Any(j => j.Status == FileTranscriptionStatus.Queued)) return Task.CompletedTask;
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCompletion = completion.Task;
        _run = cancellation;
        _ = ExecuteRunAsync(process, onAccepted, cancellation, completion);
        return completion.Task;
    }

    private async Task ExecuteRunAsync(Func<string, Action<string>, CancellationToken, Task<FileTranscriptionOutput>> process,
        Func<FileTranscriptionOutput, string?>? onAccepted,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            foreach (var job in _jobs.Where(j => j.Status == FileTranscriptionStatus.Queued).ToArray())
            {
                if (cancellation.IsCancellationRequested) break;
                job.Status = FileTranscriptionStatus.Processing; job.Stage = "Loading audio…"; Changed?.Invoke();
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var result = await process(job.Path, stage =>
                    {
                        if (ReferenceEquals(_run, cancellation) && !cancellation.IsCancellationRequested && job.Status == FileTranscriptionStatus.Processing)
                        { job.Stage = stage; Changed?.Invoke(); }
                    }, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("No speech was recognized.");
                    job.Result = result; job.Status = FileTranscriptionStatus.Ready; job.Stage = "Ready";
                    // Acceptance is final before committing usage. No await or UI notification
                    // separates this check from the commit on the owning thread.
                    string? warning = null;
                    try { warning = onAccepted?.Invoke(result); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { warning = "Result usage could not be saved. Your transcript is unchanged."; }
                    if (warning is not null) job.Result = result with
                    { Warning = result.Warning is null ? warning : result.Warning + " · " + warning };
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { job.Status = FileTranscriptionStatus.Canceled; job.Stage = "Canceled"; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { job.Status = cancellation.IsCancellationRequested ? FileTranscriptionStatus.Canceled : FileTranscriptionStatus.Failed; job.Stage = cancellation.IsCancellationRequested ? "Canceled" : ex.Message; }
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                if (cancellation.IsCancellationRequested)
                    foreach (var job in _jobs.Where(j => j.Status is FileTranscriptionStatus.Queued or FileTranscriptionStatus.Processing))
                    { job.Status = FileTranscriptionStatus.Canceled; job.Stage = "Canceled"; }
                _run = null; Changed?.Invoke();
            }
            catch (Exception ex) { failure ??= ex; }
            finally
            {
                cancellation.Dispose();
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }
    }

    /// <summary>Requests cancellation; an uninterruptible decoder is still drained.</summary>
    public void Cancel() => _run?.Cancel();
    /// <summary>Cancels and awaits the active decoder. Repeated calls await the same run; the queue remains reusable.</summary>
    public Task CancelAndDrainAsync()
    {
        var completion = _runCompletion;
        Cancel();
        return completion;
    }
    /// <summary>Permanently blocks new work, cancels, and waits for the active decoder to drain.</summary>
    public Task ShutdownAsync()
    {
        _shutdown = true;
        return CancelAndDrainAsync();
    }
    /// <summary>Requeues a failed or canceled request only when idle.</summary>
    public bool Retry(FileTranscriptionJob job)
    {
        if (_shutdown || Running || !_jobs.Contains(job) || job.Status is not (FileTranscriptionStatus.Failed or FileTranscriptionStatus.Canceled)) return false;
        job.Status = FileTranscriptionStatus.Queued; job.Stage = "Queued"; job.Result = null; Changed?.Invoke(); return true;
    }
    /// <summary>Removes a queue entry without modifying source media.</summary>
    public bool Remove(FileTranscriptionJob job)
    {
        if (Running || !_jobs.Remove(job)) return false;
        Changed?.Invoke(); return true;
    }
    /// <summary>Checks whether actual provider segments have usable finite timing.</summary>
    public static bool HasSubtitles(FileTranscriptionOutput result) => double.IsFinite(result.Duration) && result.Duration > 0 && result.Segments.Count > 0
        && result.Segments.Zip(result.Segments.Skip(1), (a, b) => b.Start >= a.Start).All(ordered => ordered) && result.Segments.All(s =>
        !string.IsNullOrWhiteSpace(s.Text) && double.IsFinite(s.Start) && double.IsFinite(s.End) && s.Start >= 0 && s.End > s.Start && s.End <= result.Duration + 1);
    /// <summary>Exports accepted text or actual provider subtitle segments.</summary>
    public static string Export(FileTranscriptionJob job, string format)
    {
        if (job.Status != FileTranscriptionStatus.Ready || job.Result is not { } result) throw new InvalidOperationException("The result is not ready.");
        if (format == "txt") return result.Text;
        if (!HasSubtitles(result)) throw new InvalidOperationException("This provider did not return usable subtitle timing.");
        return format switch { "srt" => SubtitleExporter.ToSrt(result.Segments), "vtt" => SubtitleExporter.ToWebVtt(result.Segments), _ => throw new ArgumentException("Unsupported format.", nameof(format)) };
    }
}
