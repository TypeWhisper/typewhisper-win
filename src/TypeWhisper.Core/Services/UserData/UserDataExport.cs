using System.IO.Compression;
using System.Security;
using System.Text;

namespace TypeWhisper.Core.Services.UserData;

/// <summary>What an export of all data wrote.</summary>
/// <param name="Files">Profile files copied into the archive.</param>
/// <param name="Bytes">Their combined size before compression.</param>
/// <param name="IncludesBackup">Whether the restorable backup file could be added.</param>
/// <param name="Skipped">Profile files that were in use or unreadable, and folders that could not be listed (ending in a slash), relative to the data folder.</param>
public sealed record UserDataExportResult(int Files, long Bytes, bool IncludesBackup, IReadOnlyList<string> Skipped);

/// <summary>Progress of a running export.</summary>
public readonly record struct UserDataExportProgress(int Files, long Bytes);

/// <summary>"Export all data": one .zip holding a copy of the data folder and a restorable backup.</summary>
/// <remarks>
/// The archive copies the data folder as it is, so history, audio, recordings, recaps, preferences and plugin
/// settings go with it without a list that falls behind new features. What stays out is decided by rules
/// (<see cref="IsExcluded"/>): stored secrets, license and account sign-ins, the local API's token, installed plugin
/// packages, downloaded models inside plugin folders, and internal or temporary files. Links are never followed.
///
/// The archive is written to a temporary file beside the destination and moved over it once complete, so a
/// failed or canceled export never leaves a partial file under the chosen name.
/// </remarks>
public static class UserDataExport
{
    /// <summary>The restorable backup inside the archive, in the format "Choose backup to restore" reads.</summary>
    public const string BackupEntryName = "typewhisper-backup.json";

    /// <summary>A plain-text note saying what the archive holds and what it leaves out.</summary>
    public const string ReadMeEntryName = "README.txt";

    /// <summary>The folder inside the archive that mirrors the data folder.</summary>
    public const string ProfileFolderName = "profile";

    private static readonly HashSet<string> ExcludedTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        // Installed plugin packages and the local API's upload scratch space.
        "PluginPackages", "HttpApi",
        // Licenses, account sign-in and device identity: protected to this Windows account or only valid on this PC.
        "licenses.dat", "license.json", "premium-account.dat", "premium-account-device.txt", "premium-development.txt",
        // The running local API's port and token.
        "api-port", "api-discovery.json", "api-token",
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".secret", ".dat", ".tmp", ".lock", ".partial" };

    // Already compressed; deflating them again costs time and saves nothing.
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".webm", ".mp4", ".wma", ".zip", ".png", ".jpg", ".jpeg" };

    private static readonly EnumerationOptions EveryEntry = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Writes the archive for <paramref name="profileRoot"/> to <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException">The destination is inside the data folder.</exception>
    public static async Task<UserDataExportResult> ExportAsync(string profileRoot, string destination,
        IProgress<UserDataExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("No TypeWhisper data folder was found.");
        var target = Path.GetFullPath(destination);
        // An archive inside the data folder would be copied into itself and removed by "Delete all data".
        if (IsSameOrInside(root, target))
            throw new ArgumentException("Choose a location outside the TypeWhisper data folder.", nameof(destination));
        var folder = Path.GetDirectoryName(target) ?? throw new IOException("The export destination has no folder.");

        var temporary = Path.Join(folder, ".typewhisper-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            UserDataExportResult result;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
                var backup = await TryCreateBackupAsync(root, cancellationToken).ConfigureAwait(false);
                if (backup is not null) await WriteTextAsync(archive, BackupEntryName, backup, cancellationToken).ConfigureAwait(false);
                var skipped = new List<string>();
                var files = 0;
                long bytes = 0;
                foreach (var (path, relative) in EnumerateIncludedFiles(root, skipped))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var copied = await TryCopyFileAsync(archive, path, relative, cancellationToken).ConfigureAwait(false);
                    if (copied is null) { skipped.Add(relative); continue; }
                    files++;
                    bytes += copied.Value;
                    progress?.Report(new(files, bytes));
                }
                result = new(files, bytes, backup is not null, skipped);
                await WriteTextAsync(archive, ReadMeEntryName, ReadMe(result), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            return result;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The export already succeeded or failed on its own terms; a leftover temporary is dot-named and harmless.
            }
        }
    }

    /// <summary>Whether a data-folder entry stays out of the export.</summary>
    /// <param name="relativePath">Path relative to the data folder, with either separator.</param>
    /// <param name="isDirectory">Whether the entry is a folder.</param>
    public static bool IsExcluded(string relativePath, bool isDirectory)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return true;
        // Dot-prefixed entries are internal: restore journals, staging folders, locks and the deletion marker.
        if (segments.Any(segment => segment.StartsWith('.'))) return true;
        if (ExcludedTopLevel.Contains(segments[0])) return true;
        // A plugin keeps its settings, scripts and memories as files in its own folder; subfolders hold models and runtimes.
        if (string.Equals(segments[0], "PluginData", StringComparison.OrdinalIgnoreCase) &&
            (segments.Length > 3 || segments.Length == 3 && isDirectory)) return true;
        return !isDirectory && ExcludedExtensions.Contains(Path.GetExtension(segments[^1]));
    }

    // Folders that cannot be listed go to unreadable with a trailing slash; the data folder itself failing throws.
    private static IEnumerable<(string Path, string Relative)> EnumerateIncludedFiles(string root, List<string> unreadable)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", EveryEntry); }
            catch (Exception ex) when (IsFileSystemFailure(ex) && directory != root)
            {
                // Reported, never silently dropped: an export that looks complete could be trusted as a copy.
                unreadable.Add(Path.GetRelativePath(root, directory).Replace('\\', '/') + "/");
                continue;
            }
            // Links are never followed: their target is not part of the data folder.
            foreach (var entry in entries.Where(entry => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(entry.FullName);
                if (!ProfileDataEraser.IsStrictlyInside(root, full)) continue;
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (IsExcluded(relative, isDirectory)) continue;
                if (isDirectory) pending.Push(full);
                else yield return (full, relative);
            }
        }
    }

    /// <returns>The bytes copied, or null when the file was in use, denied or gone.</returns>
    private static async Task<long?> TryCopyFileAsync(ZipArchive archive, string path, string relative, CancellationToken cancellationToken)
    {
        FileStream source;
        // Opened before the entry exists, so a file that cannot be read never leaves an empty entry behind.
        try { source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true); }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return null; }
        await using (source)
        {
            var entry = archive.CreateEntry(ProfileFolderName + "/" + relative,
                StoredExtensions.Contains(Path.GetExtension(path)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
            var modified = File.GetLastWriteTime(path);
            if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
            await using var target = entry.Open();
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            // What was read, not the current length: another writer may change the file during the copy.
            return source.Position;
        }
    }

    private static async Task<string?> TryCreateBackupAsync(string root, CancellationToken cancellationToken)
    {
        try { return await new PersistedProfileBackup(root).ExportAsync(PersistedProfileBackup.SupportedCategories, cancellationToken).ConfigureAwait(false); }
        // Too large for the restore format, or a restore awaiting recovery: the folder copy still carries the data.
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteTextAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private static string ReadMe(UserDataExportResult result)
    {
        var text = new StringBuilder()
            .Append("TypeWhisper data export\r\n")
            .Append("Created ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz")).Append("\r\n\r\n");
        if (result.IncludesBackup)
            text.Append(BackupEntryName).Append(": your dictionary, snippets, workflows and history text in TypeWhisper's backup format. ")
                .Append("Extract it and choose it under Sync & backup > Choose backup to restore.\r\n");
        else
            text.Append("The restorable backup file could not be created for this data (it may exceed the 64 MB backup limit). ")
                .Append("The same data is in the profile folder.\r\n");
        text.Append(ProfileFolderName).Append("/: a copy of your TypeWhisper data folder, including history and its audio, recordings, ")
            .Append("dictionary, snippets, workflows, preferences and plugin settings.\r\n\r\n")
            .Append("Not included: API keys and other stored secrets, licenses and account sign-ins, the local API token, ")
            .Append("downloaded models, installed plugins and temporary files.\r\n");
        if (result.Skipped.Count > 0)
        {
            text.Append("\r\nThese files and folders were in use or could not be read and are missing from this export:\r\n");
            foreach (var path in result.Skipped) text.Append("  ").Append(path).Append("\r\n");
        }
        return text.ToString();
    }

    private static bool IsSameOrInside(string root, string path)
    {
        var resolvedRoot = ResolveLinks(root);
        var resolved = ResolveLinks(path);
        return string.Equals(resolved, resolvedRoot, ProfileDataEraser.PathComparison) ||
            resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, ProfileDataEraser.PathComparison);
    }

    /// <summary>Resolves every existing link along <paramref name="path"/>, so a junction into the data folder is recognized.</summary>
    private static string ResolveLinks(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var current = Path.GetPathRoot(full) ?? "";
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                // An unreadable part is compared as written; the rest of the path is still resolved.
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool IsFileSystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;
}
