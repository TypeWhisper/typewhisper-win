using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TypeWhisper.Windows.Services;

internal static class AudioCaptureDiagnostics
{
    private static readonly object Lock = new();
    private static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("TYPEWHISPER_AUDIO_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal);

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SafePathSegment("TypeWhisper"),
        SafePathSegment("Logs"),
        SafePathSegment("audio-capture-diagnostics.log"));

    public static void Log(string message)
    {
        if (!Enabled)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var line = string.Concat(
                DateTime.UtcNow.ToString("O"),
                " [tid=",
                Environment.CurrentManagedThreadId.ToString(),
                " apt=",
                Thread.CurrentThread.GetApartmentState().ToString(),
                "] ",
                message,
                Environment.NewLine);

            lock (Lock)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics logging failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics logging failed: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics logging failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics logging failed: {ex.Message}");
        }
    }

    public static void Reset()
    {
        if (!Enabled)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, "");
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics reset failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics reset failed: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics reset failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Audio capture diagnostics reset failed: {ex.Message}");
        }
    }

    private static string SafePathSegment(string segment)
    {
        var fileName = Path.GetFileName(segment);
        return string.IsNullOrEmpty(fileName) ? string.Empty : fileName;
    }
}
