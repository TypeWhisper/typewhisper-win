using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace TypeWhisper.Core.Services.UserData;

/// <summary>What one erasure pass removed and what a second walk of the folder still found.</summary>
/// <param name="Removed">Files, folders and links deleted by this pass.</param>
/// <param name="Remaining">Entries still inside the folder afterwards, excluding kept names.</param>
/// <param name="Refused">True when the folder was not erased because it is a link, a file, a drive root or unreadable.</param>
public sealed record ProfileErasureReport(int Removed, int Remaining, bool Refused)
{
    /// <summary>True only when the walk afterwards found nothing left and the folder was not refused.</summary>
    public bool Complete => Remaining == 0 && !Refused;
}

/// <summary>Another place a pending erasure clears together with the data folder.</summary>
/// <param name="Root">A folder outside the data folder.</param>
/// <param name="Entries">Top-level names inside <paramref name="Root"/> to delete, or null to empty the whole folder.</param>
public sealed record ErasureTarget(string Root, IReadOnlyList<string>? Entries = null)
{
    /// <summary>A single file or folder, such as a log kept outside the data folder.</summary>
    public static ErasureTarget Entry(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return new(Path.GetDirectoryName(full) ?? full, [Path.GetFileName(full)]);
    }
}

/// <summary>Empties a TypeWhisper data folder for "Delete all data", keeping the folder itself.</summary>
/// <remarks>
/// Never a recursive delete of the root. Every entry must resolve strictly inside the root before it is touched,
/// links (junctions and symbolic links) are removed as links and never entered, and real folders are emptied the
/// same way before they are removed. Each folder is held open while it is emptied, so it cannot be swapped for a
/// link between the check and the listing. A root that is itself a link, a file or a drive root is refused untouched,
/// because its contents belong to whoever created the link. The report is taken from a second walk, not from the
/// delete calls: a file that is still in use stays where it is and is counted.
///
/// Files a running process keeps open (loaded models, the log) cannot be removed until that process ends. The
/// pending marker lets the next launch finish the erasure before any store opens the folder again.
/// </remarks>
public static class ProfileDataEraser
{
    /// <summary>Marker kept inside the folder while an erasure the person confirmed has not finished.</summary>
    public const string PendingMarkerName = ".data-deletion-pending";

    private static readonly EnumerationOptions EveryEntry = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Deletes every entry inside <paramref name="root"/> except the top-level names in <paramref name="keep"/>.</summary>
    public static ProfileErasureReport Erase(string root, params string[] keep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Normalize(root);
        switch (Classify(fullRoot))
        {
            case RootKind.Missing: return new(0, 0, false);
            // Not walked, not even to count: counting through a link would read its target.
            case RootKind.Refused: return new(0, 0, true);
        }

        var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        return EraseSelected(fullRoot, name => !kept.Contains(name));
    }

    /// <summary>Deletes only the top-level entries named in <paramref name="names"/> inside <paramref name="root"/>.</summary>
    /// <remarks>For folders the app shares with something else, such as an install folder that also held data.</remarks>
    public static ProfileErasureReport EraseEntries(string root, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Normalize(root);
        var selected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return Classify(fullRoot) switch
        {
            RootKind.Missing => new(0, 0, false),
            RootKind.Refused => new(0, 0, true),
            _ => EraseSelected(fullRoot, selected.Contains),
        };
    }

    private static ProfileErasureReport EraseSelected(string fullRoot, Func<string, bool> selects)
    {
        int removed;
        try
        {
            using (PinFolder(fullRoot)) removed = EmptyDirectory(fullRoot, fullRoot, selects);
        }
        // Replaced by a link or a file since it was classified.
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return new(0, 0, true); }
        return new(removed, CountRemaining(fullRoot, fullRoot, selects), false);
    }

    /// <summary>True when the folder is a plain folder or absent, the two cases <see cref="Erase"/> acts on.</summary>
    public static bool CanErase(string root) => Classify(Normalize(root)) != RootKind.Refused;

    /// <summary>Records that the person confirmed deleting everything in <paramref name="root"/>.</summary>
    /// <exception cref="IOException">The folder is a link, a file or a drive root and is never erased.</exception>
    public static void RequestErasure(string root)
    {
        var fullRoot = Normalize(root);
        if (Classify(fullRoot) == RootKind.Refused)
            throw new IOException("The TypeWhisper data folder is a link or not a folder, so it is never deleted automatically.");
        Directory.CreateDirectory(fullRoot);
        // Held open like during erasure, so the folder cannot be swapped for a link while the marker is written.
        using var pin = PinFolder(fullRoot);
        var marker = Path.Join(fullRoot, PendingMarkerName);
        // Whatever already has the marker's name, a link included, is removed as itself, and CreateNew never opens
        // an existing entry, so writing the marker cannot reach a file outside the folder.
        try
        {
            // The attributes of the entry itself: a link, even one pointing nowhere, is not followed.
            if (File.GetAttributes(marker).HasFlag(FileAttributes.Directory)) Directory.Delete(marker, recursive: false);
            else File.Delete(marker);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No marker yet, the usual case.
        }
        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
    }

    /// <summary>Whether a confirmed erasure of <paramref name="root"/> has not finished yet.</summary>
    public static bool IsErasurePending(string root)
    {
        var fullRoot = Normalize(root);
        return Classify(fullRoot) == RootKind.PlainFolder && File.Exists(Path.Join(fullRoot, PendingMarkerName));
    }

    /// <summary>Finishes a pending erasure; the marker is removed only once nothing is left in any target.</summary>
    /// <param name="root">The folder holding the marker.</param>
    /// <param name="others">Other places cleared with it, such as an older version's data folder or a log outside the folder.</param>
    /// <returns>Null when no erasure was pending.</returns>
    public static ProfileErasureReport? CompletePendingErasure(string root, params ErasureTarget[] others)
    {
        if (!IsErasurePending(root)) return null;
        var fullRoot = Normalize(root);
        // A target holding the marker's folder would take the marker with it and end the erasure half done.
        if (others.SelectMany(TargetPaths).Any(path => string.Equals(Normalize(path), fullRoot, PathComparison) || IsStrictlyInside(path, fullRoot)))
            throw new ArgumentException("Another erasure target must not contain the data folder.", nameof(others));
        var report = others.Select(other => other.Entries is null ? Erase(other.Root) : EraseEntries(other.Root, other.Entries))
            .Append(Erase(root, PendingMarkerName))
            .Aggregate((first, second) => new(first.Removed + second.Removed, first.Remaining + second.Remaining, first.Refused || second.Refused));
        if (report.Complete) CancelPendingErasure(root);
        return report;
    }

    private static IEnumerable<string> TargetPaths(ErasureTarget target) =>
        target.Entries is null ? [target.Root] : target.Entries.Select(name => Path.Join(target.Root, name));

    /// <summary>Drops the pending marker, so the next launch opens the folder as it is.</summary>
    public static void CancelPendingErasure(string root)
    {
        var fullRoot = Normalize(root);
        if (Classify(fullRoot) != RootKind.PlainFolder) return;
        var marker = Path.Join(fullRoot, PendingMarkerName);
        if (File.Exists(marker)) File.Delete(marker);
    }

    /// <summary>True when <paramref name="candidate"/> is inside <paramref name="root"/>, never the root itself or a sibling sharing its prefix.</summary>
    public static bool IsStrictlyInside(string root, string candidate)
    {
        var prefix = Normalize(root) + Path.DirectorySeparatorChar;
        var full = Normalize(candidate);
        return full.Length > prefix.Length && full.StartsWith(prefix, PathComparison);
    }

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static int EmptyDirectory(string root, string directory, Func<string, bool>? selects)
    {
        FileSystemInfo[] entries;
        // Listed completely before anything is deleted, so the walk never runs over a directory it is changing.
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", EveryEntry); }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return 0; }

        var removed = 0;
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(entry.FullName);
            if (!IsStrictlyInside(root, path) || selects?.Invoke(entry.Name) == false) continue;
            try
            {
                // Read again now: a folder replaced by a link since the listing must be removed as the link.
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path, recursive: false);
                    else File.Delete(path);
                }
                else if (attributes.HasFlag(FileAttributes.Directory))
                {
                    // Fails when the folder became a link since the check; the next pass removes it as a link.
                    using (PinFolder(path)) removed += EmptyDirectory(root, path, null);
                    ClearReadOnly(path, attributes);
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    ClearReadOnly(path, attributes);
                    File.Delete(path);
                }
                removed++;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                // In use or denied: left in place, and counted by the walk afterwards.
            }
        }
        return removed;
    }

    private static int CountRemaining(string root, string directory, Func<string, bool>? selects)
    {
        FileSystemInfo[] entries;
        // A folder that cannot be listed is not known to be empty.
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", EveryEntry); }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return 1; }

        var count = 0;
        foreach (var entry in entries.Where(entry => selects?.Invoke(entry.Name) != false))
        {
            count++;
            if (entry.Attributes.HasFlag(FileAttributes.Directory) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                IsStrictlyInside(root, entry.FullName))
                count += CountRemaining(root, Path.GetFullPath(entry.FullName), null);
        }
        return count;
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    /// <summary>Classifies the root from its own attributes, which do not follow a link.</summary>
    private static RootKind Classify(string fullRoot)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullRoot) ?? ""), fullRoot, PathComparison))
            return RootKind.Refused;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(fullRoot); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return RootKind.Missing; }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return RootKind.Refused; }
        return IsPlainFolder(attributes) ? RootKind.PlainFolder : RootKind.Refused;
    }

    /// <summary>Opens <paramref name="path"/> without following a link and fails unless it is still a plain folder.</summary>
    /// <remarks>
    /// On Windows the handle does not share delete access, so the folder cannot be renamed, removed or replaced by a
    /// link until it is closed. Elsewhere the attributes are only checked again.
    /// </remarks>
    internal static IDisposable? PinFolder(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!IsPlainFolder(File.GetAttributes(path))) throw new IOException($"'{path}' is no longer a plain folder.");
            return null;
        }

        var handle = OpenWithoutFollowing(path, ListFolder | ReadAttributes, FileShare.ReadWrite, BackupSemantics);
        try
        {
            if (!IsPlainFolder(File.GetAttributes(handle))) throw new IOException($"'{path}' is no longer a plain folder.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Opens a file for asynchronous reading, failing when <paramref name="path"/> is a link or a folder.</summary>
    /// <remarks>A file replaced by a link after it was listed is not read through the link.</remarks>
    internal static FileStream OpenFileWithoutFollowing(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"'{path}' is a link.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        }

        var handle = OpenWithoutFollowing(path, GenericRead, FileShare.ReadWrite | FileShare.Delete, Overlapped | SequentialScan);
        try
        {
            var attributes = File.GetAttributes(handle);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory))
                throw new IOException($"'{path}' is not a plain file.");
            return new FileStream(handle, FileAccess.Read, 81920, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenWithoutFollowing(string path, uint access, FileShare share, uint flags)
    {
        var handle = CreateFile(path, access, share, 0, FileMode.Open, flags | OpenReparsePoint, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException($"'{path}' could not be opened (Windows error {error}).");
    }

    private static bool IsPlainFolder(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);

    // Windows enforces sharing only against handles with data access, so attribute access alone would not pin the folder.
    private const uint ListFolder = 0x01;
    private const uint ReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint Overlapped = 0x40000000;
    private const uint SequentialScan = 0x08000000;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);

    private static bool IsFileSystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private enum RootKind { Missing, PlainFolder, Refused }
}
