using System.Diagnostics;

namespace TypeWhisper.WinUI;

internal static class PasteDiagnostics
{
    // Development-only stage markers. Never log transcript, field value, title,
    // clipboard contents, exception message, URL, or window/process identifiers.
    [Conditional("DEBUG")]
    internal static void Write(string stage, Exception? error = null)
    {
        try
        {
            var path = WinUIProfile.DataPath("paste-diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {stage}" +
                (error is null ? "" : $" {error.GetType().Name} 0x{error.HResult:X8}") + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
