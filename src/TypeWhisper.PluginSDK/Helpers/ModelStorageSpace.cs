using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Checks free storage before a large model download so a full volume fails immediately with a clear message
/// instead of minutes later with a write error.
/// </summary>
public static class ModelStorageSpace
{
    /// <summary>Free space left untouched after a download so the model does not fill the volume completely.</summary>
    public const long ReserveBytes = 256L * 1024 * 1024;

    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    /// <summary>
    /// Returns the bytes available to the current user on the volume that stores <paramref name="directory"/>,
    /// following junctions, symbolic links and mounted folders. Missing directories resolve to their nearest
    /// existing parent. Returns null when the free space cannot be determined.
    /// </summary>
    public static long? GetAvailableBytes(string directory)
    {
        try
        {
            var existing = NearestExistingDirectory(directory);
            if (existing is null) return null;
            if (OperatingSystem.IsWindows())
            {
                var path = existing.EndsWith(Path.DirectorySeparatorChar) ? existing : existing + Path.DirectorySeparatorChar;
                return GetDiskFreeSpaceEx(path, out var available, out _, out _) ? (long)Math.Min(available, (ulong)long.MaxValue) : null;
            }
            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Throws <see cref="InsufficientModelStorageException"/> when the volume that stores <paramref name="directory"/>
    /// has less than <paramref name="requiredBytes"/> plus <see cref="ReserveBytes"/> available. Nothing is checked when
    /// <paramref name="requiredBytes"/> is zero or less, or when the free space cannot be determined.
    /// </summary>
    /// <param name="directory">The directory that will receive the download, created or not.</param>
    /// <param name="requiredBytes">Bytes still to be written, including temporary archives and extracted files.</param>
    /// <param name="modelName">The model name shown to the user.</param>
    /// <param name="availableBytes">Optional free-space probe; defaults to <see cref="GetAvailableBytes"/>.</param>
    public static void EnsureAvailable(string directory, long requiredBytes, string modelName,
        Func<string, long?>? availableBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (requiredBytes <= 0) return;
        var needed = requiredBytes > long.MaxValue - ReserveBytes ? long.MaxValue : requiredBytes + ReserveBytes;
        var available = (availableBytes ?? GetAvailableBytes)(directory);
        if (available is not { } free || free >= needed) return;
        throw new InsufficientModelStorageException(modelName, DescribeLocation(directory), needed, Math.Max(0, free));
    }

    /// <summary>
    /// Deletes an incomplete download file left behind by an interrupted attempt so it no longer occupies space.
    /// A file that another writer still holds open, or a link, is kept. Returns whether the file was removed.
    /// </summary>
    public static bool TryRemoveAbandonedFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            // An active writer rejects exclusive access, so only an orphan can be opened and deleted here.
            using var orphan = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Returns whether <paramref name="exception"/> or one of its inner exceptions reports a full volume.</summary>
    public static bool IsDiskFull(Exception? exception) => FindDiskFull(exception) is not null;

    /// <summary>
    /// Returns a user-facing message for a download that failed because storage ran out, or null for other failures.
    /// Uses the message of an <see cref="InsufficientModelStorageException"/> when one is present.
    /// </summary>
    public static string? DescribeFailure(Exception? exception) => FindDiskFull(exception) switch
    {
        InsufficientModelStorageException insufficient => insufficient.Message,
        null => null,
        _ => "The disk ran out of space while downloading the model. Free up space and try again; incomplete files were not installed."
    };

    /// <summary>Formats a byte count with binary units, matching the sizes shown by File Explorer.</summary>
    public static string FormatBytes(long bytes) => FormatBytes(bytes, MidpointRounding.ToEven);

    internal static string FormatBytes(long bytes, MidpointRounding rounding)
    {
        const double Megabyte = 1024d * 1024;
        const double Gigabyte = Megabyte * 1024;
        var value = Math.Max(0, bytes);
        if (value >= Gigabyte)
            return Math.Round(value / Gigabyte, 1, rounding).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        return Math.Max(value > 0 ? 1 : 0, Math.Round(value / Megabyte, 0, rounding)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    private static Exception? FindDiskFull(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 16; depth++)
        {
            if (exception is InsufficientModelStorageException
                || exception is IOException && exception.HResult is ErrorDiskFull or ErrorHandleDiskFull)
                return exception;
            if (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    if (FindDiskFull(inner) is { } found) return found;
                return null;
            }
            exception = exception.InnerException;
        }
        return null;
    }

    private static string? NearestExistingDirectory(string directory)
    {
        for (var current = Path.GetFullPath(directory); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (Directory.Exists(current)) return current;
        return null;
    }

    private static string DescribeLocation(string directory)
    {
        try
        {
            var existing = NearestExistingDirectory(directory) ?? Path.GetFullPath(directory);
            if (OperatingSystem.IsWindows())
            {
                var volume = new char[1024];
                if (GetVolumePathName(existing, volume, volume.Length)) return new string(volume).TrimEnd((char)0);
            }
            return Path.GetPathRoot(existing) is { Length: > 0 } root ? root : existing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return directory;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);
}

/// <summary>
/// Reports that a model download was not started because its volume lacks the required free space.
/// Carries the Windows disk-full HRESULT so older hosts still classify it as a storage failure.
/// </summary>
public sealed class InsufficientModelStorageException : IOException
{
    /// <summary>Creates the exception with the amounts shown to the user.</summary>
    public InsufficientModelStorageException(string modelName, string location, long requiredBytes, long availableBytes)
        : base(BuildMessage(modelName, location, requiredBytes, availableBytes))
    {
        HResult = unchecked((int)0x80070070);
        ModelName = modelName;
        Location = location;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    /// <summary>The model whose download was refused.</summary>
    public string ModelName { get; }
    /// <summary>The volume or directory that lacks space.</summary>
    public string Location { get; }
    /// <summary>The free space the download needs, including the safety reserve.</summary>
    public long RequiredBytes { get; }
    /// <summary>The free space available when the download was checked.</summary>
    public long AvailableBytes { get; }

    private static string BuildMessage(string modelName, string location, long required, long available) =>
        $"Not enough free disk space to download {modelName}. It needs about "
        + $"{ModelStorageSpace.FormatBytes(required, MidpointRounding.ToPositiveInfinity)} on {location}, but only "
        + $"{ModelStorageSpace.FormatBytes(available, MidpointRounding.ToNegativeInfinity)} is available. Free up at least "
        + $"{ModelStorageSpace.FormatBytes(required - available, MidpointRounding.ToPositiveInfinity)} and try again.";
}
