namespace TypeWhisper.Core;

public static class TypeWhisperEnvironment
{
    public const string GithubRepoUrl = "https://github.com/TypeWhisper/typewhisper-win";

    private static readonly string _basePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper");

    public static string BasePath => _basePath;
    public static string ModelsPath => Path.Join(_basePath, "Models");
    public static string DataPath => Path.Join(_basePath, "Data");
    public static string LogsPath => Path.Join(_basePath, "Logs");
    public static string PluginsPath => Path.Join(_basePath, "Plugins");
    public static string AudioPath => Path.Join(_basePath, "Audio");
    public static string PluginDataPath => Path.Join(_basePath, "PluginData");
    public static string ApiPortFilePath => Path.Join(_basePath, "api-port");
    public static string ApiDiscoveryFilePath => Path.Join(_basePath, "api-discovery.json");
    public static string ApiTokenFilePath => Path.Join(_basePath, "api-token");
    public static string SettingsFilePath => Path.Join(_basePath, "settings.json");
    public static string DatabasePath => Path.Join(DataPath, "typewhisper.db");

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
