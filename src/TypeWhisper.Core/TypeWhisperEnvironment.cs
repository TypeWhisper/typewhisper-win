namespace TypeWhisper.Core;

/// <summary>
/// Provides type whisper environment behavior.
/// </summary>
public static class TypeWhisperEnvironment
{
    /// <summary>
    /// Defines the github repo url constant.
    /// </summary>
    public const string GithubRepoUrl = "https://github.com/TypeWhisper/typewhisper-win";

    /// <summary>
    /// Gets whether the current binary is a development build.
    /// </summary>
    public static bool IsDevelopmentBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly string _basePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IsDevelopmentBuild ? "TypeWhisper-Dev" : "TypeWhisper");

    /// <summary>
    /// Gets the base path.
    /// </summary>
    public static string BasePath => _basePath;
    /// <summary>
    /// Gets the models path.
    /// </summary>
    public static string ModelsPath => Path.Join(_basePath, "Models");
    /// <summary>
    /// Gets the data path.
    /// </summary>
    public static string DataPath => Path.Join(_basePath, "Data");
    /// <summary>
    /// Gets the logs path.
    /// </summary>
    public static string LogsPath => Path.Join(_basePath, "Logs");
    /// <summary>
    /// Gets the plugins path.
    /// </summary>
    public static string PluginsPath => Path.Join(_basePath, "Plugins");
    /// <summary>
    /// Gets the audio path.
    /// </summary>
    public static string AudioPath => Path.Join(_basePath, "Audio");
    /// <summary>
    /// Gets the plugin data path.
    /// </summary>
    public static string PluginDataPath => Path.Join(_basePath, "PluginData");
    /// <summary>
    /// Gets the api port file path.
    /// </summary>
    public static string ApiPortFilePath => Path.Join(_basePath, "api-port");
    /// <summary>
    /// Gets the api discovery file path.
    /// </summary>
    public static string ApiDiscoveryFilePath => Path.Join(_basePath, "api-discovery.json");
    /// <summary>
    /// Gets the api token file path.
    /// </summary>
    public static string ApiTokenFilePath => Path.Join(_basePath, "api-token");
    /// <summary>
    /// Gets the settings file path.
    /// </summary>
    public static string SettingsFilePath => Path.Join(_basePath, "settings.json");
    /// <summary>
    /// Gets the database path.
    /// </summary>
    public static string DatabasePath => Path.Join(DataPath, "typewhisper.db");

    /// <summary>
    /// Ensures directories.
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(AudioPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(PluginDataPath);
    }
}
