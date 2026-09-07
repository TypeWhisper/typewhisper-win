using TypeWhisper.Core.Audio;

namespace TypeWhisper.Presentation;

/// <summary>Publishes mono 16 kHz recordings atomically into an explicit profile directory.</summary>
public static class RecorderWavStore
{
    /// <summary>Returns a bounded title safe for Windows filenames on every host platform.</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var safe = new string(title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (safe.Length > 64)
        {
            safe = safe[..64];
            if (char.IsHighSurrogate(safe[^1])) safe = safe[..^1];
        }
        return safe.TrimEnd(' ', '.');
    }

    /// <summary>Writes on a worker thread; unsuccessful publication removes only its own temporary file.</summary>
    public static Task<string> SaveAsync(string directory, float[] samples, string? title = null) => Task.Run(() =>
    {
        if (samples.Length == 0) throw new InvalidOperationException("No audio was captured.");
        Directory.CreateDirectory(directory);
        var safeTitle = NormalizeTitle(title);
        var prefix = safeTitle.Length == 0 ? "recording" : $"recording-{safeTitle}";
        var path = Path.Combine(directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav");
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, WavEncoder.Encode(samples, 16000, 1));
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    });
}
