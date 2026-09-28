using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TypeWhisper.Presentation;

/// <summary>One line of the local diagnostic log. Every field is an allowlisted, content-free value.</summary>
/// <remarks>
/// Events are dotted names, data uses known keys with flag, number or short token values, and errors are exception
/// type names and HRESULTs. None of these can carry a dictated sentence, clipboard contents, a window
/// title, a path or an exception message; <see cref="DiagnosticLogFile.Admit"/> drops anything else.
/// </remarks>
public sealed record DiagnosticLogLine(
    DateTimeOffset Time,
    string Event,
    Guid? DictationId = null,
    long? ElapsedMs = null,
    IReadOnlyDictionary<string, string>? Data = null,
    string? Error = null,
    [property: JsonPropertyName("hresult")] string? HResult = null,
    IReadOnlyList<string>? Stack = null);

/// <summary>Whether the local diagnostic log is kept, and for how many days.</summary>
public sealed record DiagnosticLogPreferences(bool Enabled = true, int RetentionDays = 7)
{
    /// <summary>The retention choices offered in settings.</summary>
    public static IReadOnlyList<int> RetentionChoices { get; } = [1, 3, 7, 14, 30];

    /// <summary>Only the offered retention choices are supported.</summary>
    [JsonIgnore]
    public bool IsValid => RetentionChoices.Contains(RetentionDays);
}

/// <summary>Loads and atomically saves diagnostic log preferences in an explicitly supplied profile.</summary>
public sealed class DiagnosticLogPreferencesStore
{
    private readonly string _path;
    /// <summary>The active choice: the saved one, the defaults when none is saved, or off when it cannot be read.</summary>
    public DiagnosticLogPreferences Current { get; private set; } = new();
    /// <summary>A visible settings error. Failed loads keep the log off; failed saves keep the previous choice.</summary>
    public string? Error { get; private set; }

    /// <summary>Missing settings use the defaults without creating a file; unreadable settings turn the log off.</summary>
    public DiagnosticLogPreferencesStore(string path)
    {
        _path = path;
        try
        {
            var loaded = JsonSerializer.Deserialize<DiagnosticLogPreferences>(File.ReadAllText(path));
            if (loaded is null || !loaded.IsValid) throw new JsonException("Invalid diagnostic log settings.");
            Current = loaded;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The saved choice may have been off; never turn logging back on from settings that cannot be read.
            Current = new(Enabled: false);
            Error = "Diagnostic log settings could not be loaded, so the log is off. Turn it on to record again.";
        }
    }

    /// <summary>Persists a choice before making it active; failures preserve the previous choice.</summary>
    public string? Save(DiagnosticLogPreferences preferences)
    {
        if (!preferences.IsValid) return Error = "Choose one of the offered retention periods. Your previous choice still applies.";
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".diagnostics-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = preferences;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Diagnostic log settings could not be saved. Your previous choice still applies.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A size-capped JSON Lines file of content-free diagnostic events with day-based retention.</summary>
/// <remarks>Best-effort: file system failures never reach the caller, so logging cannot break dictation or startup.</remarks>
public sealed partial class DiagnosticLogFile
{
    /// <summary>The file is trimmed to its newest lines once it grows beyond this size.</summary>
    public const long MaximumBytes = 2 * 1024 * 1024;
    private const long TrimmedBytes = 3 * MaximumBytes / 4;
    private const long MaximumElapsedMs = 24 * 60 * 60 * 1000;
    private const int MaximumDataEntries = 16;
    private const int MaximumStackFrames = 12;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    // Only these keys are recorded, each with its own value domain, so a new key cannot carry
    // content by accident: it is dropped until it is added here.
    private static readonly FrozenDictionary<string, Func<string, bool>> DataKeys = new Dictionary<string, Func<string, bool>>
    {
        // Focus, target and capture state.
        ["accepted"] = Flag, ["current"] = Flag, ["empty"] = Flag, ["focusable"] = Flag, ["foreground"] = Flag,
        ["originalElement"] = Flag, ["ownProcess"] = Flag, ["setup"] = Flag, ["streaming"] = Flag, ["target"] = Flag,
        ["targetProcess"] = Flag, ["targetWindow"] = Flag, ["writable"] = Flag,
        // UI Automation control type ids and settings values.
        ["control"] = Number, ["retentionDays"] = Number,
        // Setting and plugin identifiers, capture decisions, and app and OS versions.
        ["arch"] = Token, ["build"] = Token, ["decision"] = Token, ["engine"] = Token, ["model"] = Token,
        ["os"] = Token, ["plugin"] = Token, ["task"] = Token, ["version"] = Token,
        ["inner"] = value => TypeName().IsMatch(value),
    }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Lock _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private DiagnosticLogPreferences _preferences;
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    /// <summary>Uses an explicit file path; nothing is read or written until the first call.</summary>
    public DiagnosticLogFile(string path, DiagnosticLogPreferences preferences, Func<DateTimeOffset>? clock = null)
    {
        FilePath = Path.GetFullPath(path);
        _preferences = preferences.IsValid ? preferences : new();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The log file.</summary>
    public string FilePath { get; }

    /// <summary>The active preferences.</summary>
    public DiagnosticLogPreferences Preferences { get { lock (_lock) return _preferences; } }

    /// <summary>Applies new preferences. Turning the log off deletes it; a shorter retention prunes it now.</summary>
    /// <returns>False when the file could not be deleted or pruned, for example while another program holds it.</returns>
    public bool Configure(DiagnosticLogPreferences preferences)
    {
        if (!preferences.IsValid) return false;
        lock (_lock)
        {
            _preferences = preferences;
            return BestEffort(() =>
            {
                if (!preferences.Enabled) File.Delete(FilePath);
                else PruneUnsafe();
            });
        }
    }

    /// <summary>Appends one line after reducing it to allowlisted values. Ignored while the log is off.</summary>
    public void Write(DiagnosticLogLine line)
    {
        var admitted = Admit(line);
        lock (_lock)
        {
            if (!_preferences.Enabled) return;
            BestEffort(() =>
            {
                if (_clock() >= _nextPrune) PruneUnsafe();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // Share reads and writes so the log can stay open in an editor while the app runs.
                using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new StreamWriter(stream, Utf8))
                    writer.Write(JsonSerializer.Serialize(admitted, Options) + "\n");
                if (new FileInfo(FilePath).Length > MaximumBytes) RewriteUnsafe(_ => true, TrimmedBytes);
            });
        }
    }

    /// <summary>Writes the header and every retained line to a new file and returns the number of retained lines.</summary>
    /// <exception cref="IOException">The destination could not be written.</exception>
    public int Export(string destination, DiagnosticLogLine header)
    {
        List<DiagnosticLogLine> lines;
        lock (_lock)
        {
            BestEffort(PruneUnsafe);
            lines = ReadUnsafe(_ => true);
        }
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, string.Concat(new[] { Admit(header) }.Concat(lines)
                .Select(line => JsonSerializer.Serialize(line, Options) + "\n")), Utf8);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return lines.Count;
    }

    /// <summary>Deletes every line. The log continues with new events while it is on.</summary>
    /// <returns>False when the file could not be deleted.</returns>
    public bool Clear()
    {
        lock (_lock) return BestEffort(() => File.Delete(FilePath));
    }

    /// <summary>Reduces a line to allowlisted values: invalid fields are dropped and an invalid event is renamed.</summary>
    public static DiagnosticLogLine Admit(DiagnosticLogLine line)
    {
        // Lines read back from disk can hold JSON nulls anywhere; they are invalid values, not errors.
        var data = line.Data?
            .Where(entry => entry.Key is not null && DataKeys.TryGetValue(entry.Key, out var valid) && entry.Value is not null && valid(entry.Value))
            .Take(MaximumDataEntries)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var stack = line.Stack?.Where(frame => frame is not null && StackFrame().IsMatch(frame)).Take(MaximumStackFrames).ToArray();
        return new(
            line.Time.ToUniversalTime(),
            line.Event is { } name && EventName().IsMatch(name) ? name : "diagnostics.invalid-event",
            line.DictationId is { } id && id != Guid.Empty ? id : null,
            line.ElapsedMs is >= 0 and <= MaximumElapsedMs ? line.ElapsedMs : null,
            data is { Count: > 0 } ? data : null,
            line.Error is { } error && TypeName().IsMatch(error) ? error : null,
            line.HResult is { } hresult && HResultValue().IsMatch(hresult) ? hresult : null,
            stack is { Length: > 0 } ? stack : null);
    }

    // Retention and the size cap rewrite the file through the same parse and admission as a write,
    // so an edited or corrupted line can never survive a prune or reach an export.
    private void PruneUnsafe()
    {
        // A failed prune is retried with the next write instead of an hour later.
        _nextPrune = DateTimeOffset.MinValue;
        if (File.Exists(FilePath))
        {
            var cutoff = _clock() - TimeSpan.FromDays(_preferences.RetentionDays);
            RewriteUnsafe(line => line.Time >= cutoff, long.MaxValue);
        }
        _nextPrune = _clock() + PruneInterval;
    }

    private void RewriteUnsafe(Func<DiagnosticLogLine, bool> keep, long maximumBytes)
    {
        var selected = new List<string>();
        long bytes = 0;
        // Keep the newest lines when the size cap applies.
        foreach (var line in Enumerable.Reverse(ReadUnsafe(keep)))
        {
            var json = JsonSerializer.Serialize(line, Options);
            bytes += Utf8.GetByteCount(json) + 1;
            if (bytes > maximumBytes) break;
            selected.Add(json);
        }
        selected.Reverse();
        var content = Utf8.GetBytes(string.Concat(selected.Select(json => json + "\n")));
        var temporary = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            try { File.Move(temporary, FilePath, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another program holds the log without sharing deletion but still lets this app append.
                // Overwrite it in place so retention and the size cap keep applying while it stays open.
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(0);
                stream.Write(content);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private List<DiagnosticLogLine> ReadUnsafe(Func<DiagnosticLogLine, bool> keep)
    {
        if (!File.Exists(FilePath)) return [];
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8);
        var lines = new List<DiagnosticLogLine>();
        while (reader.ReadLine() is { } text)
        {
            if (TryParse(text) is { } line && keep(line)) lines.Add(line);
        }
        return lines;
    }

    private static DiagnosticLogLine? TryParse(string text)
    {
        try
        {
            if (JsonSerializer.Deserialize<DiagnosticLogLine>(text, Options) is not { Event: not null } line) return null;
            var admitted = Admit(line);
            // A line that needed changes was not written by this log; drop it rather than guess.
            return admitted.Event == line.Event && admitted.Error == line.Error && admitted.HResult == line.HResult
                && admitted.ElapsedMs == line.ElapsedMs && (admitted.Data?.Count ?? 0) == (line.Data?.Count ?? 0)
                && (admitted.Stack?.Count ?? 0) == (line.Stack?.Count ?? 0) ? admitted : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool BestEffort(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    [GeneratedRegex(@"^(?=.{1,80}$)[a-z0-9]+(?:[.\-][a-z0-9]+)*$")]
    private static partial Regex EventName();
    private static bool Flag(string value) => value is "True" or "False";
    private static bool Number(string value) => NumberValue().IsMatch(value);
    private static bool Token(string value) => TokenValue().IsMatch(value);

    [GeneratedRegex(@"^[0-9]{1,10}$")]
    private static partial Regex NumberValue();
    [GeneratedRegex(@"^[A-Za-z0-9_.:+\-]{1,64}$")]
    private static partial Regex TokenValue();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.+`]{0,127}$")]
    private static partial Regex TypeName();
    [GeneratedRegex(@"^0x[0-9A-F]{8}$")]
    private static partial Regex HResultValue();
    [GeneratedRegex(@"^[A-Za-z0-9_.+`<>|$]{1,160}$")]
    private static partial Regex StackFrame();
}
