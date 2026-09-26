using System.Security.Cryptography;

namespace TypeWhisper.WinUI;

// Never open the source with SQLite: even a reader can create or change WAL sidecars.
internal static class StableImportCopy
{
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] Suffixes = ["", "-wal", "-shm", "-journal"];
    private sealed record Part(bool Exists, long Length = 0, long Written = 0, string? Hash = null);

    internal static T Read<T>(string source, Func<string, T> read, Action? afterCopy = null, string? scratchParent = null)
    {
        var directory = Path.Combine(scratchParent ?? Path.GetTempPath(), "typewhisper-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var before = Inspect(source);
                    var copy = Path.Combine(directory, attempt.ToString(), "flow.sqlite");
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
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
                            remaining -= count;
                            if (remaining < 0) throw new IOException("The source database exceeds 2 GB.");
                            output.Write(buffer, 0, count);
                        }
                    }
                    afterCopy?.Invoke();
                    var after = Inspect(source);
                    if (!before.SequenceEqual(after)) continue;
                    if (new[] { 0, 1 }.Any(i => before[i].Exists && Hash(copy + Suffixes[i], MaximumBytes) != before[i].Hash)) continue;
                    return read(copy);
                }
                catch (IOException) when (attempt < 2) { }
            }
            throw new IOException("Could not obtain a stable copy of the database. Quitting Wispr Flow and trying again can help.");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Part[] Inspect(string source)
    {
        var result = new Part[Suffixes.Length];
        long remaining = MaximumBytes;
        for (var i = 0; i < Suffixes.Length; i++)
        {
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
            result[i] = new(true, info.Length, info.LastWriteTimeUtc.Ticks, i < 2 ? Hash(info.FullName, info.Length) : null);
        }
        return result;
    }

    private static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string Hash(string path, long maximum)
    {
        using var stream = OpenShared(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            maximum -= count;
            if (maximum < 0) throw new IOException("The source changed or exceeds the import limit.");
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
