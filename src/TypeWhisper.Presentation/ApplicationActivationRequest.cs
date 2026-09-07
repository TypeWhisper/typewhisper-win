using System.Text;

namespace TypeWhisper.Presentation;

/// <summary>A bounded navigation request; files are queued for explicit processing, never automatically transcribed.</summary>
public sealed record ApplicationActivationRequest(string? Route, IReadOnlyList<string> Files, string? Error, bool ShowWindow)
{
    /// <summary>Maximum files in one activation.</summary>
    public const int MaximumFiles = 20;
    private static readonly string[] Routes = ["--account", "--sync-backup", "--dashboard", "--statistics", "--dictionary", "--snippets", "--files", "--setup", "--compare-selects", "--settings"];

    /// <summary>Parses already separated arguments, excluding the executable name.</summary>
    public static ApplicationActivationRequest Parse(IEnumerable<string> arguments, bool startup = false)
    {
        var args = arguments.Take(128).ToArray();
        if (args.Length >= 128 || args.Sum(value => (long)value.Length) > 32767) return Failure("Too many activation arguments.");
        string? route = null;
        var paths = new List<string>();
        bool minimized = startup;
        for (int i = 0; i < args.Length; i++)
        {
            var value = args[i];
            if (value.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) { minimized = true; continue; }
            if (Routes.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                var next = value.ToLowerInvariant();
                if (route is not null && route != next) return Failure("Choose one navigation destination per activation.");
                route = next; continue;
            }
            if (value.Equals("--transcribe-file", StringComparison.OrdinalIgnoreCase))
            {
                int before = paths.Count;
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    var path = args[++i];
                    if (!AbsoluteWindowsPath(path) || path.Length > 32767 || path.IndexOfAny(['\0', '"', '*', '?']) >= 0)
                        return Failure("File activation requires absolute Windows file paths.");
                    paths.Add(path);
                    if (paths.Count > MaximumFiles) return Failure("Add at most 20 files per activation.");
                }
                if (paths.Count == before) return Failure("Provide a file path after --transcribe-file.");
                continue;
            }
            // Existing visual-test flags are not navigation destinations.
            if (value == "--settings-small") continue;
            return Failure("Unknown activation argument: " + value);
        }
        if (paths.Count > 0 && route is not null && route != "--files") return Failure("File activation cannot be combined with another destination.");
        return new(paths.Count > 0 ? "--files" : route, Array.AsReadOnly(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), null,
            paths.Count > 0 || route is not null || !minimized);
    }

    private static bool AbsoluteWindowsPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
        || path.StartsWith("\\\\", StringComparison.Ordinal) && path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    private static ApplicationActivationRequest Failure(string error) => new(null, [], error, true);

    /// <summary>Tokenizes Windows command lines using the backslash-before-quote rules; the first token is the executable.</summary>
    public static ApplicationActivationRequest ParseCommandLine(string commandLine, bool startup = false)
    {
        if (commandLine.Length > 32767) return Failure("The activation command line is too long.");
        var tokens = new List<string>();
        int i = 0;
        while (i < commandLine.Length)
        {
            while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i])) i++;
            if (i == commandLine.Length) break;
            var value = new StringBuilder(); bool quoted = false;
            while (i < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[i])))
            {
                int slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) { value.Append('"'); i++; }
                    else if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { value.Append('"'); i += 2; }
                    else { quoted = !quoted; i++; }
                }
                else
                {
                    value.Append('\\', slashes);
                    if (i < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[i]))) value.Append(commandLine[i++]);
                }
            }
            tokens.Add(value.ToString());
        }
        return Parse(tokens.Skip(1), startup);
    }
}

/// <summary>Thread-safe bounded admission while the primary window is initializing.</summary>
public sealed class ActivationInbox
{
    private readonly Queue<ApplicationActivationRequest> _requests = new();
    private bool _closed;
    private bool _overflow;
    /// <summary>Admits at most eight pending activations; overflow becomes a visible error on drain.</summary>
    public void Add(ApplicationActivationRequest request)
    {
        lock (_requests)
        {
            if (_closed) return;
            if (_requests.Count >= 8) { _overflow = true; return; }
            _requests.Enqueue(request);
        }
    }
    /// <summary>Removes pending requests without executing them.</summary>
    public IReadOnlyList<ApplicationActivationRequest> Drain()
    {
        lock (_requests)
        {
            var result = _requests.ToList(); _requests.Clear();
            if (_overflow) result.Add(new(null, [], "Some activation requests were rejected because startup was busy. Retry those files.", true));
            _overflow = false; return result;
        }
    }
    /// <summary>Consumes each pending request once; one handler failure cannot discard subsequent requests.</summary>
    public void Dispatch(Action<ApplicationActivationRequest> handle, Action<Exception> reportError)
    {
        var errors = new List<Exception>();
        foreach (var request in Drain())
        {
            try { handle(request); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(ex);
            }
        }
        // Report after navigation so a successful later route cannot immediately overwrite the error.
        foreach (var error in errors)
        {
            try { reportError(error); }
            catch (Exception reportingError) when (reportingError is not OutOfMemoryException)
            { System.Diagnostics.Trace.TraceError("Activation error reporting failed: {0}", reportingError); }
        }
    }
    /// <summary>Rejects subsequent requests and discards pending work when shutting down.</summary>
    public void Close() { lock (_requests) { _closed = true; _requests.Clear(); _overflow = false; } }
}
