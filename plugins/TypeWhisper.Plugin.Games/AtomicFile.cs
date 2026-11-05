using System.Text;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Replaces a file in one step, so the game reads either the previous or the new contents and never a partial write.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    /// <summary>Replaces <paramref name="path"/> with <paramref name="contents"/> as UTF-8 without a byte order mark.</summary>
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, Utf8WithoutBom.GetBytes(contents));

    /// <summary>Writes the bytes to a temporary file beside <paramref name="path"/>, then moves it over the file.</summary>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = Path.Join(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // A failed write leaves no temporary file behind.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("A temporary add-on file could not be removed: {0}", ex.GetType().Name); }
        }
    }
}
