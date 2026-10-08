using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Local support log in every build. Stages are "event key=value …" with short token values.
// Never pass transcript, field value, title, clipboard contents, exception message, URL, path,
// or window/process identifiers; DiagnosticLogFile drops anything that is not an allowed value,
// including data keys it does not list.
//
// Callers only build their line; one background thread appends them in order. The keyboard hook,
// the capture thread and the UI thread therefore never wait for the file, its lock or the
// retention rewrite. A hook callback that waits too long makes Windows drop the hook silently.
internal static class AppDiagnostics
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly Lock Gate = new();
    private static readonly Channel<QueuedLine> Queue = Channel.CreateUnbounded<QueuedLine>(new() { SingleReader = true });
    private static volatile DiagnosticLogFile? _log;
    private static Guid? _dictation;
    private static long _dictationStarted;
    private static Timer? _pruneTimer;

    internal static DiagnosticLogPreferencesStore? Preferences { get; private set; }

    // Opening the profile may import or recover it first; nothing is written into it before then.
    internal static void Start()
    {
        lock (Gate)
        {
            if (_log is not null) return;
            Preferences = new(WinUIProfile.DataPath("diagnostics.json"));
            var log = new DiagnosticLogFile(WinUIProfile.DataPath("diagnostics.jsonl"), Preferences.Current);
            // Retries a deletion that failed when the log was turned off, and applies retention.
            // Unreadable settings only pause the log; they do not delete what it already holds.
            if (Preferences.Error is null) log.Configure(Preferences.Current);
            _log = log;
            // Retention also applies while the app runs without writing any events.
            _pruneTimer = new(_ => log.PruneIfDue(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            // A dedicated thread: a thread pool busy with model loads or decodes must not hold back the log.
            new Thread(() => WriteQueued(log)) { IsBackground = true, Name = "TypeWhisper diagnostics" }.Start();
        }
        // Earlier builds wrote full exception text here; this log replaces it.
        try { File.Delete(WinUIProfile.LegacyErrorLogPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Write($"app.start version={WindowsApplicationUpdates.CurrentVersion} os={Environment.OSVersion.Version} " +
            $"arch={RuntimeInformation.ProcessArchitecture} build={(WinUIProfile.DevelopmentBuild ? "debug" : "release")}");
    }

    internal static string? Configure(DiagnosticLogPreferences preferences)
    {
        if (Preferences is not { } store || _log is not { } log) return Loc.T("Diagnostics are not available until TypeWhisper has finished starting.");
        if (store.Save(preferences) is { } error) return error;
        // Lines queued before the change still belong to the old settings: write them first.
        Flush(FlushTimeout);
        if (log.Configure(preferences)) return null;
        return preferences.Enabled
            ? Loc.T("Your choice is saved, but the log could not be updated because another program is using it. TypeWhisper tries again with the next entry.")
            : Loc.T("The diagnostic log is off, but the existing log could not be deleted because another program is using it. TypeWhisper tries again the next time it starts.");
    }

    internal static bool Clear()
    {
        Flush(FlushTimeout);
        return _log?.Clear() ?? true;
    }

    internal static SupportDiagnosticsReport.LogInfo CaptureLog()
    {
        if (_log is not { } log) throw new InvalidOperationException();
        Flush(FlushTimeout);
        var preferences = log.Preferences;
        return new(preferences.Enabled, preferences.RetentionDays, log.Snapshot());
    }

    internal static int Export(string destination)
    {
        if (_log is not { } log) throw new InvalidOperationException("Diagnostics are not available until TypeWhisper has finished starting.");
        Flush(FlushTimeout);
        return log.Export(destination, Line($"diagnostics.export version={WindowsApplicationUpdates.CurrentVersion} " +
            $"os={Environment.OSVersion.Version} arch={RuntimeInformation.ProcessArchitecture} retentionDays={log.Preferences.RetentionDays}", null, false, default));
    }

    // Lines until EndDictation carry this id and the time since the recording started.
    internal static void BeginDictation()
    {
        lock (Gate) { _dictation = Guid.NewGuid(); _dictationStarted = Stopwatch.GetTimestamp(); }
    }

    internal static void EndDictation()
    {
        lock (Gate) _dictation = null;
    }

    // Work that finishes after its dictation ended (or while the next one runs) captures this
    // first and passes it to Write, so its lines stay attributed to the dictation that caused them.
    internal static DictationContext CurrentDictation
    {
        get { lock (Gate) return new(_dictation, _dictationStarted); }
    }

    internal static void Write(string stage, Exception? error = null)
    {
        if (_log is not null) Queue.Writer.TryWrite(new(Line(stage, error, false, CurrentDictation), null));
    }

    internal static void Write(DictationContext dictation, string stage, Exception? error = null)
    {
        if (_log is not null) Queue.Writer.TryWrite(new(Line(stage, error, false, dictation), null));
    }

    // Unhandled failures also record the method names of the failing stack, never its message.
    internal static void WriteFailure(string stage, Exception error)
    {
        if (_log is not null) Queue.Writer.TryWrite(new(Line(stage, error, true, CurrentDictation), null));
    }

    // For a process that is about to end: the queued lines are written first, then this one on the calling thread.
    internal static void WriteFailureNow(string stage, Exception error)
    {
        if (_log is not { } log) return;
        Flush(FlushTimeout);
        log.Write(Line(stage, error, true, CurrentDictation));
    }

    // Waits until every line queued so far is in the file, so exports, snapshots and shutdown include them.
    internal static bool Flush(TimeSpan timeout)
    {
        if (_log is null) return true;
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Writer.TryWrite(new(null, flushed));
        return flushed.Task.Wait(timeout);
    }

    internal readonly record struct DictationContext(Guid? Id, long Started);

    private readonly record struct QueuedLine(DiagnosticLogLine? Line, TaskCompletionSource? Flushed);

    private static void WriteQueued(DiagnosticLogFile log)
    {
        var reader = Queue.Reader;
        // The channel is never completed; the background thread ends with the process.
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (reader.TryRead(out var queued))
            {
                // The file write is best-effort already; nothing may end the only writer.
                try { if (queued.Line is { } line) log.Write(line); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                queued.Flushed?.TrySetResult();
            }
    }

    private static DiagnosticLogLine Line(string stage, Exception? error, bool stack, DictationContext dictation)
    {
        var parts = stage.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1))
            if (part.IndexOf('=') is > 0 and var split) data[part[..split]] = part[(split + 1)..];
        if (stack && error?.GetBaseException() is { } inner && inner != error && inner.GetType().FullName is { } innerType)
            data["inner"] = innerType;
        long? elapsed = dictation.Id is null ? null : (long)Stopwatch.GetElapsedTime(dictation.Started).TotalMilliseconds;
        return new(DateTimeOffset.UtcNow, parts.FirstOrDefault() ?? "", dictation.Id, elapsed, data.Count > 0 ? data : null,
            error?.GetType().FullName, error is null ? null : $"0x{error.HResult:X8}", stack && error is not null ? Frames(error) : null);
    }

    private static string[] Frames(Exception error) => new StackTrace(error, false).GetFrames()
        .Select(frame => frame.GetMethod())
        .Where(method => method is not null)
        .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
        .Take(12).ToArray();
}
