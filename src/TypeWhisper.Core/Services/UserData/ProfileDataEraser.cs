using System.Security;

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

/// <summary>Empties a TypeWhisper data folder for "Delete all data", keeping the folder itself.</summary>
/// <remarks>
/// Never a recursive delete of the root. Every entry must resolve strictly inside the root before it is touched,
/// links (junctions and symbolic links) are removed as links and never entered, and real folders are emptied the
/// same way before they are removed. A root that is itself a link, a file or a drive root is refused untouched,
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
        var removed = EmptyDirectory(fullRoot, fullRoot, kept);
        return new(removed, CountRemaining(fullRoot, fullRoot, kept), false);
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
        File.WriteAllText(Path.Combine(fullRoot, PendingMarkerName), DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>Whether a confirmed erasure of <paramref name="root"/> has not finished yet.</summary>
    public static bool IsErasurePending(string root)
    {
        var fullRoot = Normalize(root);
        return Classify(fullRoot) == RootKind.PlainFolder && File.Exists(Path.Combine(fullRoot, PendingMarkerName));
    }

    /// <summary>Finishes a pending erasure; the marker is removed only once nothing else is left.</summary>
    /// <param name="root">The folder holding the marker.</param>
    /// <param name="additionalRoots">Other folders emptied with it, such as an older version's data folder.</param>
    /// <returns>Null when no erasure was pending.</returns>
    public static ProfileErasureReport? CompletePendingErasure(string root, params string[] additionalRoots)
    {
        if (!IsErasurePending(root)) return null;
        var fullRoot = Normalize(root);
        // A folder holding the marker's folder would take the marker with it and end the erasure half done.
        if (additionalRoots.Any(other => string.Equals(Normalize(other), fullRoot, PathComparison) || IsStrictlyInside(other, fullRoot)))
            throw new ArgumentException("An additional folder must not contain the data folder.", nameof(additionalRoots));
        var report = additionalRoots.Select(other => Erase(other)).Append(Erase(root, PendingMarkerName))
            .Aggregate((first, second) => new(first.Removed + second.Removed, first.Remaining + second.Remaining, first.Refused || second.Refused));
        if (report.Complete) CancelPendingErasure(root);
        return report;
    }

    /// <summary>Drops the pending marker, so the next launch opens the folder as it is.</summary>
    public static void CancelPendingErasure(string root)
    {
        var fullRoot = Normalize(root);
        if (Classify(fullRoot) != RootKind.PlainFolder) return;
        var marker = Path.Combine(fullRoot, PendingMarkerName);
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

    private static int EmptyDirectory(string root, string directory, HashSet<string>? kept)
    {
        FileSystemInfo[] entries;
        // Listed completely before anything is deleted, so the walk never runs over a directory it is changing.
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", EveryEntry); }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return 0; }

        var removed = 0;
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(entry.FullName);
            if (!IsStrictlyInside(root, path) || kept?.Contains(entry.Name) == true) continue;
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
                    removed += EmptyDirectory(root, path, null);
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

    private static int CountRemaining(string root, string directory, HashSet<string>? kept)
    {
        FileSystemInfo[] entries;
        // A folder that cannot be listed is not known to be empty.
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", EveryEntry); }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { return 1; }

        var count = 0;
        foreach (var entry in entries)
        {
            if (kept?.Contains(entry.Name) == true) continue;
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
        return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)
            ? RootKind.PlainFolder : RootKind.Refused;
    }

    private static bool IsFileSystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private enum RootKind { Missing, PlainFolder, Refused }
}
