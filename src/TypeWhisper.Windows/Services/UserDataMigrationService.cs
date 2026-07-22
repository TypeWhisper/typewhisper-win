using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

internal static class UserDataMigrationService
{
    private const string MigrationTempSuffix = ".typewhisper-migration.tmp";
    private static readonly string[] DirectoryNames = ["Data", "Logs", "Models", "Plugins", "PluginData"];
    private static readonly string[] FileNames = ["settings.json", "settings.json.bak", "api-token"];

    public static void MigrateLegacyDataIfNeeded() =>
        MigrateLegacyData(TypeWhisperEnvironment.LegacyBasePath, TypeWhisperEnvironment.BasePath);

    internal static void MigrateLegacyData(string legacyBasePath, string userDataBasePath) =>
        MigrateLegacyData(
            legacyBasePath,
            userDataBasePath,
            Directory.Move,
            static (source, target) => File.Copy(source, target));

    internal static void MigrateLegacyData(
        string legacyBasePath,
        string userDataBasePath,
        Action<string, string> moveDirectory,
        Action<string, string> copyFile)
    {
        ArgumentNullException.ThrowIfNull(moveDirectory);
        ArgumentNullException.ThrowIfNull(copyFile);

        var legacyRoot = Normalize(legacyBasePath);
        var userDataRoot = Normalize(userDataBasePath);

        if (PathsEqual(legacyRoot, userDataRoot) || !Directory.Exists(legacyRoot))
            return;

        Directory.CreateDirectory(userDataRoot);

        foreach (var name in DirectoryNames)
            MoveWithoutOverwrite(
                Path.Join(legacyRoot, name),
                Path.Join(userDataRoot, name),
                moveDirectory,
                copyFile);

        foreach (var name in FileNames)
            MoveWithoutOverwrite(
                Path.Join(legacyRoot, name),
                Path.Join(userDataRoot, name),
                moveDirectory,
                copyFile);

        MigrateLegacyAudio(Path.Join(legacyRoot, "Audio"), Path.Join(userDataRoot, "Audio"));
    }

    private static void MoveWithoutOverwrite(
        string source,
        string target,
        Action<string, string> moveDirectory,
        Action<string, string> copyFile)
    {
        if (File.Exists(source))
        {
            if (!File.Exists(target) && !Directory.Exists(target))
                CopyFileWithoutOverwrite(source, target, copyFile);

            return;
        }

        if (!Directory.Exists(source) || File.Exists(target))
            return;

        if (!Directory.Exists(target))
        {
            try
            {
                moveDirectory(source, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(source))
                {
                    if (Directory.Exists(target))
                        return;

                    throw;
                }
            }
        }

        if (File.Exists(target))
            return;

        Directory.CreateDirectory(target);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            var destination = GetSafeChildPath(target, entry);
            MoveWithoutOverwrite(entry, destination, moveDirectory, copyFile);
        }

        TryDeleteDirectoryIfEmpty(source);
    }

    private static void CopyFileWithoutOverwrite(
        string source,
        string target,
        Action<string, string> copyFile)
    {
        if (File.Exists(target) || Directory.Exists(target))
            return;

        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("User data target has no parent directory.");
        Directory.CreateDirectory(targetDirectory);

        var temporaryTarget = target + MigrationTempSuffix;
        if (Directory.Exists(temporaryTarget))
            throw new IOException($"Migration temporary path is a directory: '{temporaryTarget}'.");

        if (File.Exists(temporaryTarget))
            File.Delete(temporaryTarget);

        try
        {
            copyFile(source, temporaryTarget);
            File.Move(temporaryTarget, target);
            File.Delete(source);
        }
        catch
        {
            TryDeleteMigrationTempFile(temporaryTarget);
            throw;
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
            throw new InvalidOperationException("Invalid user data path segment.");

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
            Trace.TraceWarning("Could not delete empty legacy user data directory '{0}': {1}", path, ex.Message);
        }
    }

    private static void TryDeleteMigrationTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not delete user data migration temporary file '{0}': {1}", path, ex.Message);
        }
    }
}
