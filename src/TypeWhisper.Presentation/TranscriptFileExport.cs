using System.Text;

namespace TypeWhisper.Presentation;

/// <summary>Collision-safe transcript publication shared by manual and automatic file processing.</summary>
public static class TranscriptFileExport
{
    /// <summary>Chooses a readable destination without replacing an existing file or directory.</summary>
    public static string Destination(string directory, string source, string format, Guid id)
    {
        if (format is not ("txt" or "srt" or "vtt")) throw new ArgumentException("Unsupported export format.", nameof(format));
        var stem = Path.GetFileNameWithoutExtension(source);
        if (stem.Length > 100) stem = stem[..100];
        var plain = Path.Combine(directory, stem + "." + format);
        if (!File.Exists(plain) && !Directory.Exists(plain)) return plain;
        return Path.Combine(directory, stem + "-" + id.ToString("N")[..8] + "." + format);
    }

    /// <summary>Atomically publishes UTF-8. An already checkpointed destination may be verified, never overwritten.</summary>
    public static async Task PublishAsync(string destination, string text, CancellationToken ct, bool verifyExisting = false)
    {
        if (File.Exists(destination))
        {
            if (verifyExisting && new FileInfo(destination).Length <= 16 * 1024 * 1024 && await File.ReadAllTextAsync(destination, ct) == text) return;
            throw new IOException("The export destination already exists. The existing file was preserved.");
        }
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".transcript-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Exports a snapshot of completed queue results; failures retain the result and do not stop other exports.</summary>
    public static async Task<IReadOnlyList<string>> ExportBatchAsync(IReadOnlyList<FileTranscriptionJob> jobs, string directory,
        string format, CancellationToken ct = default)
    {
        var completed = jobs.Where(j => j.Status == FileTranscriptionStatus.Ready && j.Result is not null).ToArray();
        var failures = new List<string>();
        foreach (var job in completed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = FileTranscriptionQueue.Export(job, format);
                await PublishAsync(Destination(directory, job.Path, format, job.Id), text, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { failures.Add(job.Name + ": " + ex.Message); }
        }
        return failures;
    }
}
