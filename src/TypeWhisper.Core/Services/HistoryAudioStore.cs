using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Audio;

namespace TypeWhisper.Core.Services;

/// <summary>Owns bounded WAV copies and a durable index of ownership and explicitly pending work.</summary>
public sealed class HistoryAudioStore
{
    private const int MaximumWaveBytes = 128 * 1024 * 1024;
    private readonly string _directory;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    /// <summary>Constructs a store without creating directories or enabling audio capture.</summary>
    public HistoryAudioStore(string directory) => _directory = Path.GetFullPath(directory);
    /// <summary>Most recent ownership, journal or cleanup failure; retry is explicit.</summary>
    public string? CleanupError { get; private set; }

    internal string Prepare(float[] samples, int sampleRate, CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (sampleRate is < 8000 or > 192000 || samples.Length == 0 ||
                samples.Length > (MaximumWaveBytes - 44) / 2 || samples.LongLength > (long)sampleRate * 3600 ||
                samples.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("History audio must be finite mono PCM within the size and duration limits.");
            var index = ReadIndex();
            var bytes = WavEncoder.Encode(samples, sampleRate);
            ct.ThrowIfCancellationRequested();
            var name = "history-" + Guid.NewGuid().ToString("N") + ".wav";
            index.Owned.Add(name, Convert.ToHexString(SHA256.HashData(bytes)));
            index.Pending.Add(name);
            WriteIndex(index); // Durable intent precedes both temporary and published audio.
            var temporary = SafePath(name + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, SafePath(name), false);
            return name;
        }
    }

    internal bool MarkDeletion(IEnumerable<string?> names)
    {
        lock (_gate)
        {
            try
            {
                var index = ReadIndex();
                foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
                {
                    if (!ValidName(name!) || !index.Owned.ContainsKey(name!))
                        throw new InvalidDataException("History references audio not owned by this store.");
                    if (!index.Pending.Contains(name!, StringComparer.Ordinal)) index.Pending.Add(name!);
                }
                if (index.Pending.Count > 0) WriteIndex(index);
                return true;
            }
            catch (Exception ex) when (Recoverable(ex))
            { CleanupError = "History audio cleanup could not be prepared. No History entries were removed."; return false; }
        }
    }

    /// <summary>Reconciles only recorded pending work; unrelated and unreferenced files are never scanned or removed.</summary>
    public string? Reconcile(IReadOnlyCollection<string> referencedFiles)
    {
        lock (_gate)
        {
            try
            {
                var index = ReadIndex();
                var references = referencedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var incomplete = false;
                foreach (var name in index.Pending.ToArray())
                {
                    try
                    {
                        var hash = index.Owned[name];
                        var path = SafePath(name);
                        var temporary = SafePath(name + ".tmp");
                        if (references.Contains(name))
                        {
                            // A missing or replaced committed file must remain visible as unavailable.
                            if (!Matches(path, hash)) throw new IOException("Referenced audio is missing or changed.");
                            DeleteVerified(temporary, hash);
                        }
                        else
                        {
                            DeleteVerified(path, hash);
                            DeleteVerified(temporary, hash);
                        }
                    }
                    catch (Exception ex) when (Recoverable(ex))
                    {
                        // One changed/partial/locked file must not block independent owned cleanup.
                        // Its ownership and pending intent remain intact for a later explicit retry.
                        incomplete = true;
                        continue;
                    }
                    if (!references.Contains(name)) index.Owned.Remove(name);
                    index.Pending.Remove(name);
                    // Journal failures remain a global stop: further mutations need durable intent.
                    WriteIndex(index);
                }
                return CleanupError = incomplete
                    ? "Some saved audio could not be removed or verified. Other pending cleanup finished; retry the remaining audio cleanup."
                    : null;
            }
            catch (Exception ex) when (Recoverable(ex))
            { return CleanupError = "Saved audio cleanup could not finish. Existing files were retained where ownership could not be verified; retry cleanup."; }
        }
    }

    /// <summary>Resolves only an existing owned file with its original content hash and safe directory chain.</summary>
    public string? Resolve(string? fileName)
    {
        if (fileName is null) return null;
        lock (_gate)
        {
            try
            {
                var index = ReadIndex();
                if (!ValidName(fileName) || !index.Owned.TryGetValue(fileName, out var hash)) return null;
                var path = SafePath(fileName);
                return Matches(path, hash) ? path : null;
            }
            catch (Exception ex) when (Recoverable(ex)) { return null; }
        }
    }

    private Index ReadIndex()
    {
        var path = SafePath("index.json");
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Audio ownership index is too large.");
        using var document = JsonDocument.Parse(stream);
        RejectDuplicateKeys(document.RootElement);
        var index = document.RootElement.Deserialize<Index>(Json) ?? throw new InvalidDataException("Missing audio ownership index.");
        if (!document.RootElement.TryGetProperty("Version", out _) || !document.RootElement.TryGetProperty("Owned", out _) ||
            !document.RootElement.TryGetProperty("Pending", out _) || index.Version != 1 || index.Owned is null || index.Pending is null ||
            index.Owned.Count > 20000 || index.Pending.Count > 20000 || index.Pending.Distinct(StringComparer.Ordinal).Count() != index.Pending.Count ||
            index.Owned.Any(pair => !ValidName(pair.Key) || pair.Value is not { Length: 64 } || !pair.Value.All(Uri.IsHexDigit)) ||
            index.Pending.Any(name => name is null || !index.Owned.ContainsKey(name)))
            throw new InvalidDataException("Invalid audio ownership index.");
        return index;
    }

    private void WriteIndex(Index index)
    {
        if (index.Owned.Count > 20000) throw new InvalidDataException("Audio ownership index is full.");
        CheckDirectory();
        Directory.CreateDirectory(_directory);
        var path = SafePath("index.json");
        var temporary = SafePath(".index-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, index, Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally
        {
            // Only this invocation's uniquely created journal temporary is eligible.
            CheckDirectory();
            if (File.Exists(temporary)) { CheckOrdinaryFile(temporary); File.Delete(temporary); }
        }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate audio index key."); RejectDuplicateKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
    private static bool ValidName(string name) => name.Length == 44 && name.StartsWith("history-", StringComparison.Ordinal) &&
        name.EndsWith(".wav", StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan(8, 32), "N", out _);
    private string SafePath(string name)
    {
        CheckDirectory();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            throw new InvalidDataException("Unsafe audio filename.");
        var path = Path.Combine(_directory, name);
        CheckOrdinaryFile(path);
        return path;
    }
    private void CheckDirectory()
    {
        for (var directory = new DirectoryInfo(_directory); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked audio directories are not supported.");
    }
    private static void CheckOrdinaryFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Linked or non-file audio paths are not supported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static bool Matches(string path, string hash)
    {
        CheckOrdinaryFile(path);
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumWaveBytes) return false;
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    private static void DeleteVerified(string path, string hash)
    {
        if (!File.Exists(path)) return;
        if (!Matches(path, hash)) throw new IOException("Audio content ownership could not be verified.");
        File.Delete(path);
    }
    internal static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException;
    private sealed class Index
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Owned { get; set; } = new(StringComparer.Ordinal);
        public List<string> Pending { get; set; } = [];
    }
}
