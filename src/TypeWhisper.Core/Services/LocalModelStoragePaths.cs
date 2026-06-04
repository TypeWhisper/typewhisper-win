using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public static class LocalModelStoragePaths
{
    public const string PluginDataFolderName = "PluginData";

    public static string DefaultModelStoragePath => TypeWhisperEnvironment.ModelsPath;

    public static string ResolveModelStoragePath(AppSettings settings) =>
        AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is { } customPath
            ? Path.GetFullPath(customPath)
            : DefaultModelStoragePath;

    public static string ResolvePluginAssetDirectory(AppSettings? settings, string pluginId)
    {
        var safePluginId = Path.GetFileName(pluginId);
        if (string.IsNullOrWhiteSpace(safePluginId))
            throw new ArgumentException("Plugin ID must not be empty.", nameof(pluginId));

        if (settings is null
            || AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            return Path.Combine(TypeWhisperEnvironment.PluginDataPath, safePluginId);
        }

        return Path.Combine(
            ResolveModelStoragePath(settings),
            PluginDataFolderName,
            safePluginId);
    }
}
