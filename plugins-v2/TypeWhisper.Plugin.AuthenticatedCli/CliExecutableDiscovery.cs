using System.IO;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed class CliExecutableDiscovery
{
    private readonly Func<EnvironmentVariableTarget, string?> _readPath;
    private readonly Func<string, IEnumerable<string>> _knownCandidates;

    internal CliExecutableDiscovery()
        : this(ReadPath, KnownCandidates)
    {
    }

    internal CliExecutableDiscovery(Func<EnvironmentVariableTarget, string?> readPath, Func<string, IEnumerable<string>>? knownCandidates = null)
    {
        _readPath = readPath;
        _knownCandidates = knownCandidates ?? (_ => []);
    }

    internal IReadOnlyList<string> FindCandidates(string executableName)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            var candidate = ResolveNativeExecutable(path, executableName);
            if (candidate is not null && seen.Add(candidate)) candidates.Add(candidate);
        }

        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var value = _readPath(target);
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (var rawDirectory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(directory) || CliPathSafety.IsNetworkOrDevicePath(directory)) continue;
                Add(Path.Combine(directory, executableName));
                // npm shims are scripts; invoke only the package's native binary directly.
                if (executableName == "opencode.exe")
                    Add(Path.Combine(directory, "node_modules", "opencode-ai", "bin", executableName));
            }
        }
        foreach (var path in _knownCandidates(executableName)) Add(path);

        return candidates;
    }

    private static IEnumerable<string> KnownCandidates(string executableName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(home, ".local", "bin", executableName);
        if (executableName == "codex.exe")
            yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", executableName);
        if (executableName == "opencode.exe")
        {
            yield return Path.Combine(home, ".opencode", "bin", executableName);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "node_modules", "opencode-ai", "bin", executableName);
        }
    }

    internal static string? ResolveNativeExecutable(string path, string executableName)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || CliPathSafety.IsNetworkOrDevicePath(path)) return null;
            var current = Path.GetFullPath(path);
            // Resolve installer junctions and symlinks, then retain only the validated local target.
            for (var attempt = 0; attempt < 32; attempt++)
            {
                if (CliPathSafety.IsNetworkOrDevicePath(current) || CliPathSafety.IsNetworkDrive(current)) return null;
                var root = Path.GetPathRoot(current)!;
                var segments = current[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var resolved = root;
                var redirected = false;
                for (var index = 0; index < segments.Length; index++)
                {
                    resolved = Path.Combine(resolved, segments[index]);
                    FileSystemInfo item = index == segments.Length - 1 ? new FileInfo(resolved) : new DirectoryInfo(resolved);
                    if (!item.Exists) return null;
                    if (!item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var target = item.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || CliPathSafety.IsNetworkOrDevicePath(target.FullName)) return null;
                    current = Path.Combine(new[] { target.FullName }.Concat(segments.Skip(index + 1)).ToArray());
                    redirected = true;
                    break;
                }
                if (!redirected) return IsSafeNativeExecutable(current, executableName) ? current : null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // Missing, cyclic or unsupported links do not become executable candidates.
        }
        return null;
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
