namespace TypeWhisper.Core;

/// <summary>
/// Provides type whisper environment behavior.
/// </summary>
public static class TypeWhisperEnvironment
{
    /// <summary>
    /// Defines the process environment variable used by debug UI automation runs.
    /// </summary>
    public const string UiAutomationDataRootEnvironmentVariable = "TYPEWHISPER_UI_AUTOMATION_DATA_ROOT";

    /// <summary>
    /// Defines the public website URL constant.
    /// </summary>
    public const string WebsiteUrl = "https://www.typewhisper.com/";

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

    private static readonly string _localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string _legacyBasePath = Path.Join(
        _localAppDataPath,
        IsDevelopmentBuild ? "TypeWhisper-Dev" : "TypeWhisper");

    private static readonly string _defaultBasePath = Path.Join(
        _localAppDataPath,
        IsDevelopmentBuild ? "TypeWhisper-DevUserData" : "TypeWhisper-UserData");

    private static string ResolveBasePath()
    {
#if DEBUG
        var automationRoot = Environment.GetEnvironmentVariable(UiAutomationDataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(automationRoot))
            return Path.GetFullPath(automationRoot);
#endif
        return _defaultBasePath;
    }

    /// <summary>
    /// Gets the canonical base path for persistent user data.
    /// </summary>
    public static string BasePath => ResolveBasePath();
    /// <summary>
    /// Gets the previous base path inside the Velopack install root.
    /// </summary>
    public static string LegacyBasePath => _legacyBasePath;
    /// <summary>
    /// Gets the base path for user-created data that must survive Velopack uninstall cleanup.
    /// </summary>
    public static string UserDataBasePath => BasePath;
    /// <summary>
    /// Gets the models path.
    /// </summary>
    public static string ModelsPath => Path.Join(BasePath, "Models");
    /// <summary>
    /// Gets the data path.
    /// </summary>
    public static string DataPath => Path.Join(BasePath, "Data");
    /// <summary>
    /// Gets the logs path.
    /// </summary>
    public static string LogsPath => Path.Join(BasePath, "Logs");
    /// <summary>
    /// Gets the plugins path.
    /// </summary>
    public static string PluginsPath => Path.Join(BasePath, "Plugins");
    /// <summary>
    /// Gets the audio path.
    /// </summary>
    public static string AudioPath => Path.Join(BasePath, "Audio");
    /// <summary>
    /// Gets the dictation recovery audio path.
    /// </summary>
    public static string DictationRecoveryPath => Path.Join(BasePath, "DictationRecovery");
    /// <summary>
    /// Gets the previous audio path inside the Velopack install root.
    /// </summary>
    public static string LegacyAudioPath => Path.Join(_legacyBasePath, "Audio");
    /// <summary>
    /// Gets the plugin data path.
    /// </summary>
    public static string PluginDataPath => Path.Join(BasePath, "PluginData");
    /// <summary>
    /// Gets the api port file path.
    /// </summary>
    public static string ApiPortFilePath => Path.Join(BasePath, "api-port");
    /// <summary>
    /// Gets the api discovery file path.
    /// </summary>
    public static string ApiDiscoveryFilePath => Path.Join(BasePath, "api-discovery.json");
    /// <summary>
    /// Gets the api token file path.
    /// </summary>
    public static string ApiTokenFilePath => Path.Join(BasePath, "api-token");
    /// <summary>
    /// Gets the settings file path.
    /// </summary>
    public static string SettingsFilePath => Path.Join(BasePath, "settings.json");
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
