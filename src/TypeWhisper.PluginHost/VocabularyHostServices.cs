using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// Restricted host for the first local vocabulary capability. Unconnected SDK
// services fail explicitly; this is not yet a general-purpose plugin host.
public sealed class VocabularyHostServices(string dataDirectory, Action<string>? diagnosticSink = null, string? assetDirectory = null, IPluginSecretStore? secrets = null) : IPluginHostServices
{
    private readonly object _sync = new();
    private string SettingsPath => Path.Combine(PluginDataDirectory, "settings.json");
    public string PluginDataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string PluginAssetDirectory => assetDirectory is null ? PluginDataDirectory : Path.GetFullPath(assetDirectory);
    public bool AllowLegacyDataMigration => false;
    public string? ActiveAppName => null;
    public string? ActiveAppProcessName => null;
    public IReadOnlyList<string> AvailableProfileNames => [];
    public event Action? CapabilitiesChanged;
    public void NotifyCapabilitiesChanged() => CapabilitiesChanged?.Invoke();
    public void Log(PluginLogLevel level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Vocabulary/{level}] {message}");
        diagnosticSink?.Invoke(message);
    }

    private Dictionary<string, JsonElement> ReadSettings() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath)) ?? throw new JsonException("Invalid plugin settings.")
        : [];
    public T? GetSetting<T>(string key)
    {
        lock (_sync) return ReadSettings().TryGetValue(key, out var value) ? value.Deserialize<T>() : default;
    }
    public void SetSetting<T>(string key, T value)
    {
        lock (_sync)
        {
            var settings = ReadSettings(); settings[key] = JsonSerializer.SerializeToElement(value);
            Directory.CreateDirectory(PluginDataDirectory);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
        }
    }
    public Task StoreSecretAsync(string key, string value) => (secrets ?? throw new NotSupportedException("Secret storage is not connected.")).StoreAsync(key, value);
    public Task<string?> LoadSecretAsync(string key) => (secrets ?? throw new NotSupportedException("Secret storage is not connected.")).LoadAsync(key);
    public Task DeleteSecretAsync(string key) => (secrets ?? throw new NotSupportedException("Secret storage is not connected.")).DeleteAsync(key);
    public IPluginEventBus EventBus => throw new NotSupportedException("Event bus is not connected in the vocabulary host.");
    public IPluginLocalization Localization => throw new NotSupportedException("Plugin localization is not connected in the vocabulary host.");
}
