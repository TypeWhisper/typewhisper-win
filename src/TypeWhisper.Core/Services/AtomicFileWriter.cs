using System.Text;

namespace TypeWhisper.Core.Services;

internal static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static bool TryWriteAllText(string filePath, string contents) =>
        TryWriteAllBytes(filePath, Utf8WithoutBom.GetBytes(contents));

    public static bool TryWriteAllBytes(string filePath, byte[] contents)
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
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
