using System.Text;

namespace TypeWhisper.Presentation;

/// <summary>A real local recording, including readable metadata or its inspection error.</summary>
public sealed record RecorderLibraryEntry(
    string FilePath, string Name, DateTimeOffset CreatedAt, long SizeBytes, TimeSpan? Duration, string? Error);

/// <summary>Enumerates and removes individual WAV files only within an explicit recording directory.</summary>
public sealed class RecorderLibraryStore(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);

    /// <summary>Reads file metadata on a worker thread without loading audio samples.</summary>
    public Task<IReadOnlyList<RecorderLibraryEntry>> ReadAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<RecorderLibraryEntry>>(() =>
    {
        if (!Directory.Exists(_directory)) return [];
        var entries = new List<RecorderLibraryEntry>();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            long size = 0;
            var created = DateTimeOffset.MinValue;
            TimeSpan? duration = null;
            string? error = null;
            try
            {
                ValidatePath(path);
                size = info.Length;
                created = info.CreationTimeUtc;
                duration = ReadDuration(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { error = ex.Message; }
            entries.Add(new(path, info.Name, created, size, duration, error));
        }
        return entries.OrderByDescending(entry => entry.CreatedAt).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }, cancellationToken);

    /// <summary>Deletes one direct child WAV, rejecting links and paths outside the recording directory.</summary>
    public Task DeleteAsync(string path) => Task.Run(() => Delete(path));

    /// <summary>Checks source use and deletes synchronously on the queue's owning thread, without yielding between them.</summary>
    public void Delete(string path, Func<string, bool>? isQueuedSource = null)
    {
        ValidatePath(path);
        if (isQueuedSource?.Invoke(path) == true)
            throw new InvalidOperationException("Remove this recording from the file queue before deleting it.");
        File.Delete(path);
    }

    private void ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullPath), _directory, comparison) ||
            !string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a WAV file from the recording library.", nameof(path));
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked recording files and directories are not supported.");
    }

    /// <summary>Revalidates the owned local WAV immediately before playback.</summary>
    public string ResolvePlaybackPath(string path)
    {
        ValidatePath(path);
        return Path.GetFullPath(path);
    }

    private static TimeSpan ReadDuration(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("The file is not a RIFF WAV recording.");
        var end = 8L + reader.ReadUInt32();
        if (end > stream.Length || end < 12 || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("The WAV header is incomplete.");
        uint sampleRate = 0;
        ushort blockAlign = 0;
        long dataBytes = 0;
        var hasData = false;
        var chunks = 0;
        while (stream.Position < end)
        {
            if (++chunks > 10000 || end - stream.Position < 8) throw new InvalidDataException("The WAV chunk table is invalid.");
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var next = stream.Position + length + (length & 1);
            if (next > end) throw new InvalidDataException("The WAV audio is truncated.");
            if (id == 0x20746d66)
            {
                if (length < 16) throw new InvalidDataException("The WAV format is incomplete.");
                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                var byteRate = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                if (format is not (1 or 3) || channels == 0 || sampleRate == 0 || bits == 0 || bits % 8 != 0 ||
                    blockAlign != channels * (bits / 8) || byteRate != (long)sampleRate * blockAlign)
                    throw new InvalidDataException("The WAV format is unsupported or invalid.");
            }
            else if (id == 0x61746164) { dataBytes += length; hasData = true; }
            stream.Position = next;
        }
        if (sampleRate == 0 || blockAlign == 0 || !hasData || dataBytes % blockAlign != 0)
            throw new InvalidDataException("The WAV recording has no valid audio data.");
        return TimeSpan.FromSeconds(dataBytes / (double)blockAlign / sampleRate);
    }
}
