using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Local support log in every build. Stages are "event key=value …" with short token values.
// Never pass transcript, field value, title, clipboard contents, exception message, URL, path,
// or window/process identifiers; DiagnosticLogFile drops anything that is not an allowed value,
// including data keys it does not list.
internal static class AppDiagnostics
{
    private static readonly Lock Gate = new();
    private static volatile DiagnosticLogFile? _log;
    private static Guid? _dictation;
    private static long _dictationStarted;

    internal static DiagnosticLogPreferencesStore? Preferences { get; private set; }

    // Opening the profile may import or recover it first; nothing is written into it before then.
    internal static void Start()
    {
        lock (Gate)
        {
            if (_log is not null) return;
            Preferences = new(WinUIProfile.DataPath("diagnostics.json"));
            _log = new(WinUIProfile.DataPath("diagnostics.jsonl"), Preferences.Current);
            // Retries a deletion that failed when the log was turned off, and applies retention.
            // Unreadable settings only pause the log; they do not delete what it already holds.
            if (Preferences.Error is null) _log.Configure(Preferences.Current);
        }
        Write($"app.start version={WindowsApplicationUpdates.CurrentVersion} os={Environment.OSVersion.Version} " +
            $"arch={RuntimeInformation.ProcessArchitecture} build={(WinUIProfile.DevelopmentBuild ? "debug" : "release")}");
    }

    internal static string? Configure(DiagnosticLogPreferences preferences)
    {
        if (Preferences is not { } store || _log is not { } log) return "Diagnostics are not available until TypeWhisper has finished starting.";
        if (store.Save(preferences) is { } error) return error;
        if (log.Configure(preferences)) return null;
        return preferences.Enabled
            ? "Your choice is saved, but the log could not be updated because another program is using it. TypeWhisper tries again with the next entry."
            : "The diagnostic log is off, but the existing log could not be deleted because another program is using it. TypeWhisper tries again the next time it starts.";
    }

    internal static bool Clear() => _log?.Clear() ?? true;

    internal static int Export(string destination)
    {
        if (_log is not { } log) throw new InvalidOperationException("Diagnostics are not available until TypeWhisper has finished starting.");
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

    internal static void Write(string stage, Exception? error = null) => _log?.Write(Line(stage, error, false, CurrentDictation));

    internal static void Write(DictationContext dictation, string stage, Exception? error = null) => _log?.Write(Line(stage, error, false, dictation));

    // Unhandled failures also record the method names of the failing stack, never its message.
    internal static void WriteFailure(string stage, Exception error) => _log?.Write(Line(stage, error, true, CurrentDictation));

    internal readonly record struct DictationContext(Guid? Id, long Started);

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
