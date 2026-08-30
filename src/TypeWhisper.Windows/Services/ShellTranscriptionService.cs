using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Registers the Explorer transcription verb and transfers shell activations
/// to the primary TypeWhisper instance.
/// </summary>
internal static class ShellTranscriptionService
{
    internal const string CommandLineSwitch = "--transcribe-file";
    private const string VerbName = "TypeWhisper.Transcribe";
    private const string RequestDirectoryName = "shell-transcription-requests";

    internal static IReadOnlyList<string> ParseFilePaths(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            for (index++; index < args.Count && !args[index].StartsWith("--", StringComparison.Ordinal); index++)
            {
                var path = args[index];
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    paths.Add(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    Debug.WriteLine($"Ignored invalid shell transcription path: {ex.Message}");
                }
            }

            index--;
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool Enqueue(IReadOnlyCollection<string> paths, string? requestDirectory = null)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
            return false;

        try
        {
            var directory = requestDirectory ?? GetRequestDirectory();
            Directory.CreateDirectory(directory);
            var requestName = $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
            var temporaryPath = Path.Combine(directory, $"{requestName}.tmp");
            var requestPath = Path.Combine(directory, $"{requestName}.json");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalizedPaths));
            File.Move(temporaryPath, requestPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not queue shell transcription request: {ex.Message}");
            return false;
        }
    }

    internal static IReadOnlyList<string> Drain(string? requestDirectory = null)
    {
        var directory = requestDirectory ?? GetRequestDirectory();
        if (!Directory.Exists(directory))
            return [];

        string[] requestFiles;
        try
        {
            requestFiles = Directory.EnumerateFiles(directory, "*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not enumerate shell transcription requests: {ex.Message}");
            return [];
        }

        var paths = new List<string>();
        foreach (var requestFile in requestFiles)
        {
            try
            {
                var requestPaths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(requestFile));
                if (requestPaths is not null)
                    paths.AddRange(requestPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Debug.WriteLine($"Could not read shell transcription request: {ex.Message}");
            }
            finally
            {
                try
                {
                    File.Delete(requestFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not remove shell transcription request: {ex.Message}");
                }
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool HasPendingRequests(string? requestDirectory = null)
    {
        try
        {
            var directory = requestDirectory ?? GetRequestDirectory();
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not inspect shell transcription requests: {ex.Message}");
            return false;
        }
    }

    internal static void EnsureContextMenuRegistration(string displayName)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(displayName))
            return;

        try
        {
            foreach (var extension in AudioFileService.SupportedFileExtensions)
            {
                using var verbKey = Registry.CurrentUser.CreateSubKey(GetVerbRegistryPath(extension), writable: true);
                if (verbKey is null)
                    continue;

                verbKey.SetValue(string.Empty, displayName);
                verbKey.SetValue("Icon", $"\"{executablePath}\",0");
                verbKey.SetValue("MultiSelectModel", "Player");

                using var commandKey = verbKey.CreateSubKey("command", writable: true);
                commandKey?.SetValue(string.Empty, BuildCommand(executablePath));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine($"Explorer context menu registration failed: {ex.Message}");
        }
    }

    internal static void RemoveContextMenuRegistration()
    {
        try
        {
            foreach (var extension in AudioFileService.SupportedFileExtensions)
                Registry.CurrentUser.DeleteSubKeyTree(GetVerbRegistryPath(extension), throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Debug.WriteLine($"Explorer context menu cleanup failed: {ex.Message}");
        }
    }

    internal static string BuildCommand(string executablePath) =>
        $"\"{executablePath}\" {CommandLineSwitch} \"%1\"";

    private static string GetRequestDirectory() =>
        Path.Combine(TypeWhisperEnvironment.DataPath, RequestDirectoryName);

    private static string GetVerbRegistryPath(string extension) =>
        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbName}";
}
