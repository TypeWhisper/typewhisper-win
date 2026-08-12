using System.IO;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed class CliExecutableDiscovery
{
    private readonly Func<EnvironmentVariableTarget, string?> _readPath;

    internal CliExecutableDiscovery()
        : this(ReadPath)
    {
    }

    internal CliExecutableDiscovery(Func<EnvironmentVariableTarget, string?> readPath)
    {
        _readPath = readPath;
    }

    internal IReadOnlyList<string> FindCandidates(string executableName)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine
                 })
        {
            var value = _readPath(target);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var rawDirectory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                if (!CliPathSafety.IsSafeLocalDirectory(directory))
                    continue;

                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (!IsSafeNativeExecutable(candidate, executableName) || !seen.Add(candidate))
                    continue;

                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    internal static bool IsSafeNativeExecutable(string path, string executableName)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;

            var fullPath = Path.GetFullPath(path);
            if (CliPathSafety.IsNetworkOrDevicePath(fullPath)
                || CliPathSafety.IsNetworkDrive(fullPath)
                || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(fullPath), executableName, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)
                || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var directory = Directory.GetParent(fullPath);
            while (directory is not null && directory.Parent is not null)
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
                directory = directory.Parent;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadPath(EnvironmentVariableTarget target) =>
        target == EnvironmentVariableTarget.Process
            ? Environment.GetEnvironmentVariable("PATH")
            : Environment.GetEnvironmentVariable("PATH", target);
}

internal static class CliPathSafety
{
    internal static bool IsSafeLocalDirectory(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;

            var fullPath = Path.GetFullPath(path);
            if (IsNetworkOrDevicePath(fullPath)
                || IsNetworkDrive(fullPath)
                || !Directory.Exists(fullPath))
            {
                return false;
            }

            var directory = new DirectoryInfo(fullPath);
            while (directory is not null)
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
                directory = directory.Parent;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal)
        || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
        || path.StartsWith("\\\\.\\", StringComparison.Ordinal);

    internal static bool IsNetworkDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is null || new DriveInfo(root).DriveType == DriveType.Network;
    }
}
