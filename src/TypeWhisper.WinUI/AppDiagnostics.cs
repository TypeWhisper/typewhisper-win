using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Local support log in every build. Stages are "event key=value …" with short token values.
// Never pass transcript, field value, title, clipboard contents, exception message, URL, path,
// or window/process identifiers; DiagnosticLogFile drops anything that is not an allowed value.
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
        }
        Write($"app.start version={WindowsApplicationUpdates.CurrentVersion} os={Environment.OSVersion.Version} " +
            $"arch={RuntimeInformation.ProcessArchitecture} build={(WinUIProfile.DevelopmentBuild ? "debug" : "release")}");
    }

    internal static string? Configure(DiagnosticLogPreferences preferences)
    {
        if (Preferences is not { } store || _log is not { } log) return "Diagnostics are not available until TypeWhisper has finished starting.";
        if (store.Save(preferences) is { } error) return error;
        log.Configure(preferences);
        return null;
    }

    internal static void Clear() => _log?.Clear();

    internal static int Export(string destination)
    {
        if (_log is not { } log) throw new InvalidOperationException("Diagnostics are not available until TypeWhisper has finished starting.");
        return log.Export(destination, Line($"diagnostics.export version={WindowsApplicationUpdates.CurrentVersion} " +
            $"os={Environment.OSVersion.Version} arch={RuntimeInformation.ProcessArchitecture} retentionDays={log.Preferences.RetentionDays}", null, false));
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

    internal static void Write(string stage, Exception? error = null) => _log?.Write(Line(stage, error, false));

    // Unhandled failures also record the method names of the failing stack, never its message.
    internal static void WriteFailure(string stage, Exception error) => _log?.Write(Line(stage, error, true));

    private static DiagnosticLogLine Line(string stage, Exception? error, bool stack)
    {
        var parts = stage.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1))
            if (part.IndexOf('=') is > 0 and var split) data[part[..split]] = part[(split + 1)..];
        if (stack && error?.GetBaseException() is { } inner && inner != error && inner.GetType().FullName is { } innerType)
            data["inner"] = innerType;
        Guid? dictation;
        long? elapsed = null;
        lock (Gate)
        {
            dictation = _dictation;
            if (dictation is not null) elapsed = (long)Stopwatch.GetElapsedTime(_dictationStarted).TotalMilliseconds;
        }
        return new(DateTimeOffset.UtcNow, parts.FirstOrDefault() ?? "", dictation, elapsed, data.Count > 0 ? data : null,
            error?.GetType().FullName, error is null ? null : $"0x{error.HResult:X8}", stack && error is not null ? Frames(error) : null);
    }

    private static string[] Frames(Exception error) => new StackTrace(error, false).GetFrames()
        .Select(frame => frame.GetMethod())
        .Where(method => method is not null)
        .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
        .Take(12).ToArray();
}
