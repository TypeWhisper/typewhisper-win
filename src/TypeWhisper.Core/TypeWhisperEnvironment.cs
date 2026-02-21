namespace TypeWhisper.Core;

public static class TypeWhisperEnvironment
{
    public const string GithubRepoUrl = "https://github.com/TypeWhisper/typewhisper-win";

    private static readonly string _basePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper");

    public static string BasePath => _basePath;
    public static string ModelsPath => Path.Combine(_basePath, "Models");
    public static string DataPath => Path.Combine(_basePath, "Data");
    public static string LogsPath => Path.Combine(_basePath, "Logs");
    public static string PluginsPath => Path.Combine(_basePath, "Plugins");
    public static string PluginDataPath => Path.Combine(_basePath, "PluginData");
    public static string SettingsFilePath => Path.Combine(_basePath, "settings.json");
    public static string DatabasePath => Path.Combine(DataPath, "typewhisper.db");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(PluginDataPath);
    }
}
