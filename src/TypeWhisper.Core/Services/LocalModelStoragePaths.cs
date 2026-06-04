using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Resolves storage paths for large local model assets.
/// </summary>
public static class LocalModelStoragePaths
{
    /// <summary>
    /// Gets the plugin asset folder name under custom model storage.
    /// </summary>
    public const string PluginDataFolderName = "PluginData";

    /// <summary>
    /// Gets the default local model storage path.
    /// </summary>
    public static string DefaultModelStoragePath => TypeWhisperEnvironment.ModelsPath;

    /// <summary>
    /// Resolves the active local model storage path.
    /// </summary>
    public static string ResolveModelStoragePath(AppSettings settings) =>
        AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is { } customPath
            ? Path.GetFullPath(customPath)
            : DefaultModelStoragePath;

    /// <summary>
    /// Resolves the active plugin asset directory for large model and runtime files.
    /// </summary>
    public static string ResolvePluginAssetDirectory(AppSettings? settings, string pluginId)
    {
        var safePluginId = Path.GetFileName(pluginId.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safePluginId) || safePluginId is "." or "..")
            throw new ArgumentException("Plugin ID must not be empty.", nameof(pluginId));

        if (settings is null
            || AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            return Path.Join(TypeWhisperEnvironment.PluginDataPath, safePluginId);
        }

        return Path.Join(
            ResolveModelStoragePath(settings),
            Path.GetFileName(PluginDataFolderName),
            safePluginId);
    }
}
