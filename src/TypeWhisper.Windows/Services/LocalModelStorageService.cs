using System.IO;
using System.Globalization;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Manages custom storage for large local model assets.
/// </summary>
public sealed class LocalModelStorageService
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PluginAssetEntries =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.typewhisper.whisper-cpp"] = ["Models"],
            ["com.typewhisper.sherpa-onnx"] = ["Models", "Runtimes"],
            ["com.typewhisper.gemma-local"] = ["Models"],
            ["com.typewhisper.supertonic-tts"] = ["Models"],
            ["com.typewhisper.granite-speech"] =
            [
                "python",
                "hf-cache",
                ".setup-complete",
                "python-embed.zip",
                "get-pip.py"
            ]
        };

    private readonly ISettingsService _settings;
    private readonly Action? _unloadActiveModels;

    /// <summary>
    /// Initializes a new instance of the LocalModelStorageService class.
    /// </summary>
    public LocalModelStorageService(ISettingsService settings, Action? unloadActiveModels = null)
    {
        _settings = settings;
        _unloadActiveModels = unloadActiveModels;
    }

    /// <summary>
    /// Gets the currently resolved local model storage path.
    /// </summary>
    public string ResolvedModelStoragePath =>
        LocalModelStoragePaths.ResolveModelStoragePath(_settings.Current);

    /// <summary>
    /// Gets the default local model storage path.
    /// </summary>
    public static string DefaultModelStoragePath => LocalModelStoragePaths.DefaultModelStoragePath;

    /// <summary>
    /// Resolves and validates the active local model storage path.
    /// </summary>
    public static string ResolveAvailableModelStoragePath(AppSettings settings)
    {
        var root = LocalModelStoragePaths.ResolveModelStoragePath(settings);
        if (AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            Directory.CreateDirectory(root);
            return root;
        }

        EnsureExistingWritableCustomRoot(root);
        return root;
    }

    /// <summary>
    /// Resolves and validates the active plugin asset directory.
    /// </summary>
    public static string ResolveAvailablePluginAssetDirectory(AppSettings? settings, string pluginId)
    {
        var directory = LocalModelStoragePaths.ResolvePluginAssetDirectory(settings, pluginId);
        if (settings is null
            || AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            Directory.CreateDirectory(directory);
            return directory;
        }

        ResolveAvailableModelStoragePath(settings);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Moves known large local model assets to the target path and saves it as the active storage path.
    /// </summary>
    public async Task MoveDownloadsAndUsePathAsync(string targetPath, CancellationToken ct = default)
    {
        var targetRoot = PrepareWritableTarget(targetPath);
        var sourceRoot = ResolvedModelStoragePath;

        if (PathsEqual(sourceRoot, targetRoot))
        {
            _settings.Save(_settings.Current with { LocalModelStoragePath = targetRoot });
            return;
        }

        _unloadActiveModels?.Invoke();

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            MigrateModelRootContents(sourceRoot, targetRoot, ct);
            MigratePluginAssets(sourceRoot, targetRoot, ct);
        }, ct);

        _settings.Save(_settings.Current with { LocalModelStoragePath = targetRoot });
    }

    /// <summary>
    /// Resets local model storage to the default app data path.
    /// </summary>
    public void ResetToDefault() =>
        _settings.Save(_settings.Current with { LocalModelStoragePath = null });

    private static string PrepareWritableTarget(string targetPath)
    {
        var normalized = AppSettings.NormalizeLocalModelStoragePath(targetPath)
            ?? throw new ArgumentException(Loc.Instance["Models.StoragePathRequired"], nameof(targetPath));

        var fullPath = Path.GetFullPath(normalized);
        Directory.CreateDirectory(fullPath);

        EnsureWritable(fullPath);

        return fullPath;
    }

    private static void EnsureExistingWritableCustomRoot(string fullPath)
    {
        if (!Directory.Exists(fullPath))
        {
            throw new LocalModelStorageUnavailableException(
                FormatStorageMessage(
                    "Models.StorageMissingFormat",
                    "Model storage folder does not exist: {0}",
                    fullPath));
        }

        EnsureWritable(fullPath);
    }

    private static void EnsureWritable(string fullPath)
    {
        var probePath = Path.Join(fullPath, $".typewhisper-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "");
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalModelStorageUnavailableException(
                FormatStorageMessage(
                    "Models.StorageUnwritableFormat",
                    "Model storage folder is not writable: {0}",
                    fullPath),
                ex);
        }
    }

    private static string FormatStorageMessage(string key, string fallbackFormat, string path)
    {
        var localized = Loc.Instance.GetString(key, path);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? string.Format(CultureInfo.InvariantCulture, fallbackFormat, path)
            : localized;
    }

    private static void MigrateModelRootContents(string sourceRoot, string targetRoot, CancellationToken ct)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        Directory.CreateDirectory(targetRoot);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (string.Equals(name, LocalModelStoragePaths.PluginDataFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            MoveEntry(entry, Path.Join(targetRoot, SafeLeafName(name, nameof(entry))));
        }
    }

    private static void MigratePluginAssets(string sourceRoot, string targetRoot, CancellationToken ct)
    {
        var pluginDataFolderName = SafeRelativeName(LocalModelStoragePaths.PluginDataFolderName, nameof(LocalModelStoragePaths.PluginDataFolderName));

        foreach (var (pluginId, entries) in PluginAssetEntries)
        {
            ct.ThrowIfCancellationRequested();
            var pluginFolderName = SafeLeafName(pluginId, nameof(pluginId));
            var sourcePluginDir = Path.Join(sourceRoot, pluginDataFolderName, pluginFolderName);
            if (!Directory.Exists(sourcePluginDir))
                continue;

            var targetPluginDir = Path.Join(targetRoot, pluginDataFolderName, pluginFolderName);
            foreach (var entryName in entries)
            {
                ct.ThrowIfCancellationRequested();
                var safeEntryName = SafeRelativeName(entryName, nameof(entryName));
                var sourceEntry = Path.Join(sourcePluginDir, safeEntryName);
                var targetEntry = Path.Join(targetPluginDir, safeEntryName);
                MoveEntry(sourceEntry, targetEntry);
            }
        }
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                MoveEntry(child, Path.Join(target, SafeLeafName(Path.GetFileName(child), nameof(child))));

            TryDeleteDirectoryIfEmpty(source);
            return;
        }

        if (!File.Exists(source) || File.Exists(target))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        File.Delete(source);
    }

    private static string SafeLeafName(string value, string parameterName)
    {
        var safeName = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            throw new InvalidOperationException($"Invalid path segment for {parameterName}.");

        return safeName;
    }

    private static string SafeRelativeName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Invalid relative path segment for {parameterName}.");
        }

        return value;
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not delete empty model storage directory '{0}': {1}",
                path,
                ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not delete empty model storage directory '{0}': {1}",
                path,
                ex.Message);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
