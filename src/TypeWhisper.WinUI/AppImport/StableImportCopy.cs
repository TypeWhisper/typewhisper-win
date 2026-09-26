using System.Security.Cryptography;

namespace TypeWhisper.WinUI;

// Never open the source with SQLite: even a reader can create or change WAL sidecars.
internal static class StableImportCopy
{
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] Suffixes = ["", "-wal", "-shm", "-journal"];
    private sealed record Part(bool Exists, long Length = 0, long Written = 0, string? Hash = null);

    private const string ScratchPrefix = "typewhisper-import-";

    internal static T Read<T>(string source, Func<string, T> read, Action? afterCopy = null, string? scratchParent = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupAbandonedCopies(scratchParent);
        var directory = Path.Combine(scratchParent ?? Path.GetTempPath(), ScratchPrefix + Guid.NewGuid().ToString("N"));
        FileStream? lease = null;
        try
        {
            Directory.CreateDirectory(directory);
            lease = new FileStream(Path.Combine(directory, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            // Only acquisition retries. Invalid schemas and row limits in a stable copy fail once.
            var copy = Acquire(source, directory, afterCopy, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return read(copy);
        }
        finally
        {
            lease?.Dispose();
            TryDelete(directory);
        }
    }

    private static string Acquire(string source, string directory, Action? afterCopy, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptDirectory = Path.Combine(directory, attempt.ToString());
            try
            {
                var before = Inspect(source, cancellationToken);
                var copy = Path.Combine(attemptDirectory, "flow.sqlite");
                Directory.CreateDirectory(attemptDirectory);
                long remaining = MaximumBytes;
                foreach (var index in new[] { 0, 1 })
                {
                    if (!before[index].Exists) continue;
                    using var input = OpenShared(source + Suffixes[index]);
                    using var output = new FileStream(copy + Suffixes[index], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        remaining -= count;
                        if (remaining < 0) throw new IOException("The source database exceeds 2 GB.");
                        output.Write(buffer, 0, count);
                    }
                }
                afterCopy?.Invoke();
                var after = Inspect(source, cancellationToken);
                if (before.SequenceEqual(after) && !new[] { 0, 1 }.Any(i => before[i].Exists &&
                    Hash(copy + Suffixes[i], MaximumBytes, cancellationToken) != before[i].Hash)) return copy;
            }
            catch (IOException) when (attempt < 2) { }
            TryDelete(attemptDirectory);
        }
        throw new IOException("Could not obtain a stable copy of the database. Quitting Wispr Flow and trying again can help.");
    }

    private static Part[] Inspect(string source, CancellationToken cancellationToken)
    {
        var result = new Part[Suffixes.Length];
        long remaining = MaximumBytes;
        for (var i = 0; i < Suffixes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(source + Suffixes[i]);
            if (!info.Exists)
            {
                if (i == 0) throw new FileNotFoundException("The source database was not found.");
                result[i] = new(false);
                continue;
            }
            if (i == 3 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The source database cannot be copied safely. Quitting Wispr Flow and trying again can help.");
            remaining -= info.Length;
            if (remaining < 0) throw new IOException("The source database exceeds 2 GB.");
            result[i] = new(true, info.Length, info.LastWriteTimeUtc.Ticks, i < 2 ? Hash(info.FullName, info.Length, cancellationToken) : null);
        }
        return result;
    }

    private static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string Hash(string path, long maximum, CancellationToken cancellationToken)
    {
        using var stream = OpenShared(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            maximum -= count;
            if (maximum < 0) throw new IOException("The source changed or exceeds the import limit.");
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Retry cleanup on launch and later imports. Age protects folder creation; the lease protects active imports.
    internal static void CleanupAbandonedCopies(string? parent = null)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(parent ?? Path.GetTempPath(), ScratchPrefix + "*"))
            {
                try
                {
                    var info = new DirectoryInfo(path);
                    if (!Guid.TryParseExact(info.Name[ScratchPrefix.Length..], "N", out _) ||
                        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
                    using (new FileStream(Path.Combine(path, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                    TryDelete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
