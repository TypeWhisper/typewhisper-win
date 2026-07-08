using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

internal static class UserAudioMigrationService
{
    public static void MigrateLegacyAudioIfNeeded()
    {
        try
        {
            MigrateLegacyAudio(TypeWhisperEnvironment.LegacyAudioPath, TypeWhisperEnvironment.AudioPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not migrate legacy audio directory: {0}", ex.Message);
        }
    }

    internal static void MigrateLegacyAudio(string legacyAudioPath, string audioPath)
    {
        var legacyRoot = Normalize(legacyAudioPath);
        var audioRoot = Normalize(audioPath);

        if (PathsEqual(legacyRoot, audioRoot) || !Directory.Exists(legacyRoot))
            return;

        if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
        {
            TryDeleteDirectoryIfEmpty(legacyRoot);
            return;
        }

        Directory.CreateDirectory(audioRoot);
        foreach (var entry in Directory.GetFileSystemEntries(legacyRoot))
            MoveEntry(entry, GetSafeChildPath(audioRoot, entry));

        TryDeleteDirectoryIfEmpty(legacyRoot);
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source))
        {
            var resolvedTarget = ResolveUniquePath(target);
            Directory.CreateDirectory(resolvedTarget);

            foreach (var child in Directory.GetFileSystemEntries(source))
                MoveEntry(child, GetSafeChildPath(resolvedTarget, child));

            TryDeleteDirectoryIfEmpty(source);
            return;
        }

        if (!File.Exists(source))
            return;

        var fileTarget = ResolveUniquePath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(fileTarget)!);
        File.Move(source, fileTarget);
    }

    private static string ResolveUniquePath(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
            return target;

        var directory = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        for (var index = 1; ; index++)
        {
            var candidate = Path.Join(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static string GetSafeChildPath(string directory, string path) =>
        Path.Join(directory, SafeLeafName(Path.GetFileName(path)));

    private static string SafeLeafName(string value)
    {
        var safeName = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            throw new InvalidOperationException("Invalid audio path segment.");

        return safeName;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not delete empty legacy audio directory '{0}': {1}", path, ex.Message);
        }
    }
}
