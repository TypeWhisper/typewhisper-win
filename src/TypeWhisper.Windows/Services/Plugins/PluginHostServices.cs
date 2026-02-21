using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Per-plugin host services implementation. Each plugin gets its own instance
/// with isolated settings storage and secret management scoped to its plugin ID.
/// </summary>
public sealed class PluginHostServices : IPluginHostServices
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const string SecretPrefix = "secret:";

    private readonly string _pluginId;
    private readonly IActiveWindowService _activeWindow;
    private readonly IPluginEventBus _eventBus;
    private readonly IProfileService _profiles;
    private readonly string _settingsFilePath;
    private readonly string _pluginDataDirectory;
    private readonly object _settingsLock = new();

    private Dictionary<string, JsonElement>? _settingsCache;

    public PluginHostServices(
        string pluginId,
        IActiveWindowService activeWindow,
        IPluginEventBus eventBus,
        IProfileService profiles)
    {
        _pluginId = pluginId;
        _activeWindow = activeWindow;
        _eventBus = eventBus;
        _profiles = profiles;
        _pluginDataDirectory = Path.Combine(Core.TypeWhisperEnvironment.PluginDataPath, pluginId);
        _settingsFilePath = Path.Combine(_pluginDataDirectory, "settings.json");
    }

    public string PluginDataDirectory
    {
        get
        {
            Directory.CreateDirectory(_pluginDataDirectory);
            return _pluginDataDirectory;
        }
    }

    public string? ActiveAppProcessName => _activeWindow.GetActiveWindowProcessName();
    public string? ActiveAppName => _activeWindow.GetActiveWindowTitle();

    public IPluginEventBus EventBus => _eventBus;

    public IReadOnlyList<string> AvailableProfileNames =>
        _profiles.Profiles.Select(p => p.Name).ToList();

    public void Log(PluginLogLevel level, string message)
    {
        Debug.WriteLine($"[Plugin:{_pluginId}] [{level}] {message}");
    }

    #region Secrets (DPAPI-backed)

    public Task StoreSecretAsync(string key, string value)
    {
        var encrypted = ApiKeyProtection.Encrypt(value);
        var settings = LoadSettings();
        lock (_settingsLock)
        {
            settings[$"{SecretPrefix}{key}"] = JsonSerializer.SerializeToElement(encrypted);
            SaveSettings(settings);
        }
        return Task.CompletedTask;
    }

    public Task<string?> LoadSecretAsync(string key)
    {
        var settings = LoadSettings();
        var secretKey = $"{SecretPrefix}{key}";
        if (settings.TryGetValue(secretKey, out var element))
        {
            var encrypted = element.Deserialize<string>();
            if (encrypted is not null)
            {
                return Task.FromResult<string?>(ApiKeyProtection.Decrypt(encrypted));
            }
        }
        return Task.FromResult<string?>(null);
    }

    public Task DeleteSecretAsync(string key)
    {
        var settings = LoadSettings();
        lock (_settingsLock)
        {
            settings.Remove($"{SecretPrefix}{key}");
            SaveSettings(settings);
        }
        return Task.CompletedTask;
    }

    #endregion

    #region Settings (JSON-backed)

    public T? GetSetting<T>(string key)
    {
        var settings = LoadSettings();
        if (settings.TryGetValue(key, out var element))
        {
            try
            {
                return element.Deserialize<T>(JsonOptions);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[Plugin:{_pluginId}] Failed to deserialize setting '{key}': {ex.Message}");
            }
        }
        return default;
    }

    public void SetSetting<T>(string key, T value)
    {
        var settings = LoadSettings();
        lock (_settingsLock)
        {
            settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);
            SaveSettings(settings);
        }
    }

    private Dictionary<string, JsonElement> LoadSettings()
    {
        lock (_settingsLock)
        {
            if (_settingsCache is not null)
                return _settingsCache;

            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    _settingsCache = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions) ?? [];
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Plugin:{_pluginId}] Failed to load settings: {ex.Message}");
                    _settingsCache = [];
                }
            }
            else
            {
                _settingsCache = [];
            }

            return _settingsCache;
        }
    }

    private void SaveSettings(Dictionary<string, JsonElement> settings)
    {
        try
        {
            Directory.CreateDirectory(_pluginDataDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Plugin:{_pluginId}] Failed to save settings: {ex.Message}");
        }
    }

    #endregion
}
