using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Persisted file request state; loading never starts a request.</summary>
public enum FileTranscriptionRecoveryStatus
{
    /// <summary>Waiting for an explicit start.</summary>
    Queued,
    /// <summary>A request was in progress at the checkpoint.</summary>
    Processing,
    /// <summary>A previous process ended during work; explicit retry is required.</summary>
    Interrupted,
    /// <summary>Accepted text is available offline.</summary>
    Ready,
    /// <summary>The request was canceled.</summary>
    Canceled,
    /// <summary>The request failed and may be retried explicitly.</summary>
    Failed
}

/// <summary>Records whether acceptance side effects finished; a pending receipt is never replayed automatically.</summary>
public enum FileTranscriptionAcceptanceReceipt
{
    /// <summary>No result was accepted.</summary>
    None,
    /// <summary>Text was checkpointed, but history and usage completion cannot be established after restart.</summary>
    Pending,
    /// <summary>The acceptance callback returned, including any recoverable warnings.</summary>
    Completed
}

/// <summary>File metadata captured before processing; this is a change detector, not a content hash.</summary>
public sealed record FileTranscriptionSourceFingerprint(long SizeBytes, DateTime LastWriteTimeUtc)
{
    /// <summary>Reads metadata without decoding or copying source audio; missing files throw.</summary>
    public static FileTranscriptionSourceFingerprint Capture(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Checks whether an existing source still has the captured size and modification time.</summary>
    public bool Matches(string path) => this == Capture(path);
}

/// <summary>Exportable result data only. It deliberately contains no history payload or snippet usage IDs.</summary>
public sealed record FileTranscriptionRecoveryResult(string Text, string Provider, string Model, double Duration,
    IReadOnlyList<TranscriptionSegment> Segments, string? Warning = null, string? DisplayName = null)
{
    /// <summary>Copies accepted text and genuine provider segments without retaining pending side effects.</summary>
    public static FileTranscriptionRecoveryResult FromOutput(FileTranscriptionOutput output) =>
        new(output.Text, output.Provider, output.Model, output.Duration,
            Array.AsReadOnly(output.Segments.ToArray()), output.Warning, output.DisplayName);

    /// <summary>Creates an exportable result with no side effects to replay.</summary>
    public FileTranscriptionOutput ToOutput() => new(Text, Provider, Model, Duration,
        Array.AsReadOnly(Segments.ToArray()), Warning) { DisplayName = DisplayName };
}

/// <summary>A stable file request checkpoint; the source belongs to the user and is never modified by this store.</summary>
public sealed record FileTranscriptionRecoveryEntry(Guid Id, string SourcePath,
    FileTranscriptionSourceFingerprint? Source, FileTranscriptionRecoveryStatus Status,
    FileTranscriptionRecoveryResult? Result = null,
    FileTranscriptionAcceptanceReceipt Receipt = FileTranscriptionAcceptanceReceipt.None, string? Stage = null)
{
    /// <summary>Explains interruption or uncertain persistence without claiming successful side effects.</summary>
    [JsonIgnore]
    public string? RecoveryNotice => Status == FileTranscriptionRecoveryStatus.Interrupted
        ? "Processing was interrupted. Choose Retry to transcribe this file again."
        : Receipt == FileTranscriptionAcceptanceReceipt.Pending
            ? "The transcript was recovered. History and snippet usage completion could not be verified; these actions were not repeated."
            : null;
}

/// <summary>Explicit, opt-in file queue checkpoints. Reads never start decoding or perform acceptance side effects.</summary>
/// <remarks>One host owns this store. Missing files default to disabled. Invalid files remain untouched and block writes.</remarks>
public sealed class FileTranscriptionQueueStore
{
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumCharacters = 2_000_000;
    private readonly string _path;
    private bool _writable = true;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private sealed record Document(int Version, bool Enabled, IReadOnlyList<FileTranscriptionRecoveryEntry> Jobs);

    /// <summary>Loads an explicit profile path without writing or inspecting the referenced media files.</summary>
    public FileTranscriptionQueueStore(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            if (!File.Exists(_path))
            {
                if (Directory.Exists(_path)) throw new IOException("The recovery file is a directory.");
                return;
            }
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("The recovery snapshot is too large.");
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateFields(json.RootElement);
            ValidateDocumentShape(json.RootElement);
            var document = json.RootElement.Deserialize<Document>(Options)
                ?? throw new InvalidDataException("The recovery snapshot is empty.");
            if (document.Version != SchemaVersion) throw new InvalidDataException("Unsupported file recovery schema.");
            var jobs = ValidateAndCopy(document.Jobs);
            if (!document.Enabled && jobs.Count != 0) throw new InvalidDataException("Disabled recovery contains saved jobs.");
            Enabled = document.Enabled;
            Entries = Array.AsReadOnly(jobs.Select(job => job.Status == FileTranscriptionRecoveryStatus.Processing
                ? job with { Status = FileTranscriptionRecoveryStatus.Interrupted } : job).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            Enabled = false; Entries = []; _writable = false;
            Error = "File queue recovery could not be loaded. The existing file was preserved: " + ex.Message;
        }
    }

    /// <summary>The last successfully loaded or persisted option; false until explicitly enabled.</summary>
    public bool Enabled { get; private set; }
    /// <summary>An owned immutable snapshot; restored processing entries require explicit retry.</summary>
    public IReadOnlyList<FileTranscriptionRecoveryEntry> Entries { get; private set; } = [];
    /// <summary>Actionable load or write failure; a failed write never claims a new option or checkpoint.</summary>
    public string? Error { get; private set; }

    /// <summary>Persists explicit consent. Turning it off atomically removes saved queue data, never the source files.</summary>
    public bool TrySetEnabled(bool enabled, IReadOnlyList<FileTranscriptionRecoveryEntry>? entries = null) =>
        Write(enabled, enabled ? entries ?? Entries : []);

    /// <summary>Explicitly replaces even an invalid checkpoint with disabled, empty recovery data. Requires a separate user-confirmed discard action.</summary>
    public bool TryDiscardSavedData()
    {
        var previous = _writable;
        _writable = true;
        if (Write(false, [])) return true;
        _writable = previous;
        return false;
    }

    /// <summary>Checkpoints at most twenty jobs only when the user has enabled recovery.</summary>
    public bool TrySave(IReadOnlyList<FileTranscriptionRecoveryEntry> entries)
    {
        if (!Enabled) { Error ??= "File queue recovery is off."; return false; }
        return Write(true, entries);
    }

    private bool Write(bool enabled, IReadOnlyList<FileTranscriptionRecoveryEntry> entries)
    {
        if (!_writable) return false;
        string? temporary = null;
        try
        {
            var owned = ValidateAndCopy(entries);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(SchemaVersion, enabled, owned), Options);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("The recovery snapshot is too large.");
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Enabled = enabled; Entries = owned; Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { Error = "File queue recovery could not be saved. The previous setting and checkpoint remain in effect: " + ex.Message; return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<FileTranscriptionRecoveryEntry> ValidateAndCopy(IReadOnlyList<FileTranscriptionRecoveryEntry> entries)
    {
        if (entries is null || entries.Count > 20) throw new InvalidDataException("The recovery queue supports at most twenty jobs.");
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copy = new List<FileTranscriptionRecoveryEntry>();
        var characters = 0;
        var segments = 0;
        void Text(string? text, int maximum, bool required = false)
        {
            if ((required && string.IsNullOrWhiteSpace(text)) || text?.Length > maximum)
                throw new InvalidDataException("Invalid or oversized recovery text.");
            characters += text?.Length ?? 0;
            if (characters > MaximumCharacters) throw new InvalidDataException("The recovery snapshot text is too large.");
        }
        foreach (var entry in entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) ||
                !Enum.IsDefined(entry.Status) || !Enum.IsDefined(entry.Receipt)) throw new InvalidDataException("Invalid or duplicate recovery job.");
            Text(entry.SourcePath, 32767, true);
            Text(entry.Stage, 16384);
            if (!Path.IsPathFullyQualified(entry.SourcePath) || !paths.Add(Path.GetFullPath(entry.SourcePath)) ||
                !FileTranscriptionQueue.Extensions.Contains(Path.GetExtension(entry.SourcePath), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid or duplicate source path.");
            if (entry.Source is { } source && (source.SizeBytes < 0 || source.LastWriteTimeUtc.Kind != DateTimeKind.Utc))
                throw new InvalidDataException("Invalid source fingerprint.");
            if (entry.Source is null && entry.Status is FileTranscriptionRecoveryStatus.Processing or FileTranscriptionRecoveryStatus.Interrupted)
                throw new InvalidDataException("A started request requires a source fingerprint.");
            var ready = entry.Status == FileTranscriptionRecoveryStatus.Ready;
            if (ready != (entry.Result is not null) || ready != (entry.Receipt != FileTranscriptionAcceptanceReceipt.None))
                throw new InvalidDataException("Inconsistent recovery result and receipt.");
            if (entry.Result is { } result)
            {
                Text(result.Text, 1_000_000, true); Text(result.Provider, 1024, true); Text(result.Model, 1024, true);
                Text(result.Warning, 16384); Text(result.DisplayName, 2048);
                if (!double.IsFinite(result.Duration) || result.Duration <= 0 || result.Duration > 3601)
                    throw new InvalidDataException("Invalid result duration.");
                if (result.Segments is null || result.Segments.Any(segment => segment is null) ||
                    result.Segments.Count > 0 && !FileTranscriptionQueue.HasSubtitles(result.ToOutput()))
                {
                    const string warning = "Invalid subtitle timing was omitted from recovery. TXT remains available.";
                    result = result with { Segments = [], Warning = result.Warning is null ? warning : result.Warning[..Math.Min(result.Warning.Length, 16000)] + " · " + warning };
                }
                if (result.Segments.Count > 10000 - segments) throw new InvalidDataException("Too many recovery subtitle segments.");
                segments += result.Segments.Count;
                foreach (var segment in result.Segments)
                {
                    Text(segment.Text, 20000, true);
                }
                copy.Add(entry with { Result = result with { Segments = Array.AsReadOnly(result.Segments.ToArray()) } });
            }
            else copy.Add(entry);
        }
        return copy.AsReadOnly();
    }

    private static void ValidateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate recovery JSON field.");
                ValidateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateFields(item);
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        RequireFields(root, "Version", "Enabled", "Jobs");
        if (root.GetProperty("Jobs").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid recovery jobs.");
        foreach (var entry in root.GetProperty("Jobs").EnumerateArray())
        {
            RequireFields(entry, "Id", "SourcePath", "Source", "Status", "Result", "Receipt");
            var source = entry.GetProperty("Source");
            if (source.ValueKind != JsonValueKind.Null) RequireFields(source, "SizeBytes", "LastWriteTimeUtc");
            var result = entry.GetProperty("Result");
            if (result.ValueKind == JsonValueKind.Null) continue;
            RequireFields(result, "Text", "Provider", "Model", "Duration", "Segments");
            if (result.GetProperty("Segments").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid recovery segments.");
            foreach (var segment in result.GetProperty("Segments").EnumerateArray()) RequireFields(segment, "Text", "Start", "End");
        }
    }

    private static void RequireFields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || names.Any(name => !element.TryGetProperty(name, out _)))
            throw new InvalidDataException("Incomplete recovery metadata.");
    }
}
