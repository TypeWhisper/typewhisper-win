namespace TypeWhisper.Core.Services.Sync;

/// <summary>
/// Limits for reading the shared sync folder. Other devices and other people with access to the folder write it,
/// so its files are read with fixed bounds, and linked folders or files are not followed.
/// </summary>
public static class CloudFolderSyncFiles
{
    /// <summary>Operation and device files above this size are skipped. Real ones are a few kilobytes.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>At most this many operation files are read per device folder, the newest by file name.</summary>
    public const int MaxOperationFilesPerDevice = 20_000;

    /// <summary>Changes dated further ahead than this are ignored until the local clock reaches them.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Returns whether a change time is not implausibly far in the future.</summary>
    public static bool IsPlausible(DateTime? changedAt, DateTime now) =>
        changedAt is not { } value || Utc(value) <= Utc(now) + MaxClockSkew;

    /// <summary>Returns whether a folder or file is a symbolic link or junction. Cloud placeholders are not links.</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable link data is not followed either.
            return true;
        }
    }

    /// <summary>Throws when a folder the app writes into is a link, so no files are written elsewhere.</summary>
    public static void EnsureNotLinked(string directory)
    {
        if (IsLink(new DirectoryInfo(directory)))
            throw new IOException(Loc.T("The sync folder contains a linked folder. Remove the link or choose another sync folder."));
    }

    /// <summary>Returns whether a folder between <paramref name="root"/> and <paramref name="path"/> is a link.</summary>
    public static bool HasLinkedFolder(string root, string path)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(path)); directory is not null &&
                 directory.StartsWith(top, StringComparison.OrdinalIgnoreCase); directory = Path.GetDirectoryName(directory))
        {
            if (IsLink(new DirectoryInfo(directory))) return true;
            if (directory.Length == top.Length) break;
        }
        return false;
    }

    /// <summary>The device folders below <paramref name="operationsPath"/>, without linked folders.</summary>
    public static IEnumerable<string> DeviceDirectories(string operationsPath)
    {
        var root = new DirectoryInfo(operationsPath);
        if (!root.Exists || IsLink(root)) yield break;
        foreach (var device in root.EnumerateDirectories())
            if (!IsLink(device)) yield return device.FullName;
    }

    /// <summary>
    /// The operation files of one device folder in file-name order, without linked files. When a folder holds more
    /// than <see cref="MaxOperationFilesPerDevice"/> files, only the newest by file name are returned.
    /// </summary>
    public static IReadOnlyList<string> OperationFiles(string deviceDirectory)
    {
        var files = new DirectoryInfo(deviceDirectory).EnumerateFiles("*.json")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Take(MaxOperationFilesPerDevice)
            .Where(file => !IsLink(file))
            .Select(file => file.FullName)
            .ToList();
        files.Reverse();
        return files;
    }

    /// <summary>Reads a sync file, or returns null when it is linked or larger than <see cref="MaxFileBytes"/>.</summary>
    public static string? ReadSmallFile(string path)
    {
        if (IsLink(new FileInfo(path))) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxFileBytes) return null;
        // Read only the size seen above, even if the file grows meanwhile; a cut-off file fails to parse.
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        using var reader = new StreamReader(new MemoryStream(bytes));
        return reader.ReadToEnd();
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
