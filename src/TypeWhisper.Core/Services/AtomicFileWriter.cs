using System.Text;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Replaces a file in one step, so readers see either the previous or the new contents and never a partial write.
/// </summary>
/// <remarks>
/// The contents go to a uniquely named temporary file beside the target, are flushed to disk and then moved over
/// the target. A missing directory is created, and the temporary file is removed when the write fails.
/// </remarks>
public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    /// <summary>Replaces <paramref name="filePath"/> with <paramref name="contents"/> as UTF-8 without a byte order mark.</summary>
    /// <param name="filePath">The file to create or replace.</param>
    /// <param name="contents">The new text of the file.</param>
    /// <exception cref="IOException">The file could not be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file or its directory was denied.</exception>
    public static void WriteAllText(string filePath, string contents) =>
        WriteAllBytes(filePath, Utf8WithoutBom.GetBytes(contents));

    /// <summary>Replaces <paramref name="filePath"/> with <paramref name="contents"/>.</summary>
    /// <param name="filePath">The file to create or replace.</param>
    /// <param name="contents">The new bytes of the file.</param>
    /// <exception cref="IOException">The file could not be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file or its directory was denied.</exception>
    public static void WriteAllBytes(string filePath, byte[] contents)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            temporaryPath = Path.Combine(
                directory ?? Directory.GetCurrentDirectory(),
                $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            // One unbuffered write followed by a flush to disk keeps the replacement durable
            // without a synchronous write-through round trip for every small block.
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 0))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    internal static bool TryWriteAllText(string filePath, string contents) =>
        TryWriteAllText(filePath, contents, out _);

    /// <summary>Replaces <paramref name="filePath"/> with <paramref name="contents"/> as UTF-8 without a byte order mark and reports why a write failed.</summary>
    /// <param name="filePath">The file to create or replace.</param>
    /// <param name="contents">The new text of the file.</param>
    /// <param name="error">The exception that stopped the write, or null when the file was replaced.</param>
    /// <returns>True when the file was replaced.</returns>
    public static bool TryWriteAllText(string filePath, string contents, out Exception? error) =>
        TryWriteAllBytes(filePath, Utf8WithoutBom.GetBytes(contents), out error);

    internal static bool TryWriteAllBytes(string filePath, byte[] contents) =>
        TryWriteAllBytes(filePath, contents, out _);

    /// <summary>Replaces <paramref name="filePath"/> with <paramref name="contents"/> and reports why a write failed.</summary>
    /// <param name="filePath">The file to create or replace.</param>
    /// <param name="contents">The new bytes of the file.</param>
    /// <param name="error">The exception that stopped the write, or null when the file was replaced. Callers use it to tell a locked file from a full disk.</param>
    /// <returns>True when the file was replaced.</returns>
    public static bool TryWriteAllBytes(string filePath, byte[] contents, out Exception? error)
    {
        try
        {
            WriteAllBytes(filePath, contents);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}
