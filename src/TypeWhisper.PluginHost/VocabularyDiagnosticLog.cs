using System.Text.Json;

namespace TypeWhisper.PluginHost;

// Local bounded diagnostic journal. Callers supply metadata only, never audio,
// transcripts, vocabulary text or exception messages. Logging cannot fail dictation.
public sealed class VocabularyDiagnosticLog(string path)
{
    private readonly object _sync = new();
    public void Write(string message)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                    File.Move(path, path + ".previous", true);
                var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, message = message[..Math.Min(message.Length, 2048)] });
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
