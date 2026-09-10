using System.Text;
using System.Runtime.InteropServices;

namespace TypeWhisper.Plugin.Obsidian;

// Creates one complete new note. No append, overwrite, or URL launch is performed.
internal static class ObsidianNoteWriter
{
    internal static Task<string> WriteAsync(string vault, string subfolder, string filename, string content,
        CancellationToken ct, Func<Task>? beforeCommit = null, Action? afterCommit = null) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        var directory = ResolveDirectory(vault, subfolder, create: true);
        var name = SafeFilename(filename);
        var temporary = Path.Combine(directory, ".typewhisper-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            if (beforeCommit is not null) await beforeCommit();
            ct.ThrowIfCancellationRequested();
            // Recheck existing directory components immediately before committing.
            ResolveDirectory(vault, subfolder, create: false);
            for (var suffix = 1; suffix <= 1000; suffix++)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, name + (suffix == 1 ? "" : " " + suffix) + ".md");
                try { CommitWithoutOverwrite(temporary, path); }
                catch (IOException) when (File.Exists(path)) { continue; }
                // Cancellation after this atomic commit cannot undo or obscure the saved note.
                afterCommit?.Invoke();
                return path;
            }
            throw new IOException("No unused note filename is available.");
        }
        finally
        {
            // A failed commit recheck can mean a directory was replaced with a link.
            // Never follow that changed path to clean up a same-named foreign file.
            try
            {
                var verified = ResolveDirectory(vault, subfolder, create: false);
                if (string.Equals(verified, directory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Leave our temporary file behind when cleanup cannot establish a safe path.
                // Preserve the original cancellation/commit failure instead of masking it.
            }
        }
    }, ct);

    private static void CommitWithoutOverwrite(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(temporary, destination, overwrite: false);
            return;
        }
        // Unix File.Move can check existence before rename, whose replacement
        // semantics race with another writer. link publishes the complete file
        // atomically and fails if the destination already exists. Both paths are
        // in the same directory/filesystem; finally removes our temporary name.
        if (Link(temporary, destination) != 0)
            throw new IOException($"Could not publish the note (errno {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    internal static string ResolveDirectory(string vault, string subfolder, bool create)
    {
        if (string.IsNullOrWhiteSpace(vault) || !Path.IsPathFullyQualified(vault) || !Directory.Exists(vault))
            throw new InvalidDataException("Choose an existing absolute vault directory.");
        if (Path.IsPathRooted(subfolder) || subfolder.Contains(':'))
            throw new InvalidDataException("The note folder must be relative to the vault.");
        var components = subfolder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".." || component != SafeDirectoryComponent(component)))
            throw new InvalidDataException("The note folder contains unsupported path components.");
        var current = Path.GetFullPath(vault);
        CheckDirectory(current);
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
            CheckDirectory(current);
        }
        return current;
    }

    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The vault and note folder must be existing ordinary directories, without links.");
    }

    private static string SafeDirectoryComponent(string value) => SafeFilename(value);

    internal static string SafeFilename(string value)
    {
        var text = new string(value.Select(character => character < ' ' || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (text.Length > 160) text = text[..160].TrimEnd('.', ' ');
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
        if (string.IsNullOrWhiteSpace(text)) text = "Transcription";
        var stem = text.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            text = "_" + text;
        return text;
    }
}
