using System.Collections.Concurrent;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// Restricted host for the first local vocabulary capability. Unconnected SDK
// services fail explicitly; this is not yet a general-purpose plugin host.
public sealed class VocabularyHostServices(string dataDirectory, Action<string>? diagnosticSink = null, string? assetDirectory = null, IPluginSecretStore? secrets = null,
    Func<PluginLlmStatus?>? defaultLlm = null) : IPluginHostServices
{
    public PluginLlmStatus? DefaultLlm => defaultLlm?.Invoke();
    // The parsed settings of one file, shared by every host of that folder in this process, so a write through one
    // host is what the next read of any of them returns. The file is read again once its write time or size changed.
    private sealed class SettingsFile
    {
        internal Dictionary<string, JsonElement>? Values;
        internal (DateTime Written, long Length) Stamp;
    }
    private static readonly ConcurrentDictionary<string, SettingsFile> SettingsFiles =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SettingsFile _settings = SettingsFiles.GetOrAdd(Path.Combine(Path.GetFullPath(dataDirectory), "settings.json"), _ => new());
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

    // Callers hold the file's lock. The returned values are shared and must not be changed.
    private Dictionary<string, JsonElement> ReadSettings()
    {
        // The stamp is taken before the read: a file replaced in between is read again next time.
        var file = new FileInfo(SettingsPath);
        if (!file.Exists) { _settings.Values = null; return []; }
        var stamp = (file.LastWriteTimeUtc, file.Length);
        if (_settings.Values is { } cached && _settings.Stamp == stamp) return cached;
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath)) ?? throw new JsonException("Invalid plugin settings.");
        (_settings.Values, _settings.Stamp) = (values, stamp);
        return values;
    }
    public T? GetSetting<T>(string key)
    {
        lock (_settings) return ReadSettings().TryGetValue(key, out var value) ? value.Deserialize<T>() : default;
    }
    public void SetSetting<T>(string key, T value)
    {
        lock (_settings)
        {
            var current = ReadSettings();
            var element = JsonSerializer.SerializeToElement(value);
            // A value the file already holds is not written again, such as Enabled on every start.
            if (current.TryGetValue(key, out var saved) && saved.GetRawText() == element.GetRawText()) return;
            var settings = new Dictionary<string, JsonElement>(current) { [key] = element };
            Directory.CreateDirectory(PluginDataDirectory);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(settings));
            // Moving the file keeps its time and size, so the stamp is read from the temporary file before it replaces the old one.
            var written = new FileInfo(SettingsPath + ".tmp");
            var stamp = (written.LastWriteTimeUtc, written.Length);
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
            // Published only once the file holds the new values.
            (_settings.Values, _settings.Stamp) = (settings, stamp);
        }
    }
    public Task StoreSecretAsync(string key, string value) => (secrets ?? throw new NotSupportedException("Secret storage is not connected.")).StoreAsync(key, value);
    public Task<string?> LoadSecretAsync(string key) => (secrets ?? throw new NotSupportedException("Secret storage is not connected.")).LoadAsync(key);
    public Task DeleteSecretAsync(string key) => (secrets ?? throw new NotSupportedException("Secret storage is not connected.")).DeleteAsync(key);
    public IPluginEventBus EventBus => throw new NotSupportedException("Event bus is not connected in the vocabulary host.");
    public IPluginLocalization Localization => throw new NotSupportedException("Plugin localization is not connected in the vocabulary host.");
}
