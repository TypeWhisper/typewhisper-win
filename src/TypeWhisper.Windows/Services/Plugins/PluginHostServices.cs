using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using AppLocalization = TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Per-plugin host services implementation. Each plugin gets its own instance
/// with isolated settings storage and secret management scoped to its plugin ID.
/// </summary>
public sealed class PluginHostServices : IPluginHostServices, ILivePreviewAppearanceProvider
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
    private readonly IWorkflowService _workflows;
    private readonly Action? _onCapabilitiesChanged;
    private readonly ISettingsService? _settings;
    private readonly PluginLocalization _localization;
    private readonly string _settingsFilePath;
    private readonly string _pluginDataDirectory;
    private readonly object _settingsLock = new();

    private Dictionary<string, JsonElement>? _settingsCache;

    /// <summary>
    /// Initializes a new instance of the PluginHostServices class.
    /// </summary>
    public PluginHostServices(
        string pluginId,
        string pluginDirectory,
        IActiveWindowService activeWindow,
        IPluginEventBus eventBus,
        IWorkflowService workflows,
        Action? onCapabilitiesChanged = null,
        ISettingsService? settings = null)
    {
        _pluginId = pluginId;
        _activeWindow = activeWindow;
        _eventBus = eventBus;
        _workflows = workflows;
        _onCapabilitiesChanged = onCapabilitiesChanged;
        _settings = settings;
        _localization = new PluginLocalization(pluginDirectory, AppLocalization.Loc.Instance.CurrentLanguage);
        _pluginDataDirectory = Path.Combine(Core.TypeWhisperEnvironment.PluginDataPath, pluginId);
        _settingsFilePath = Path.Combine(_pluginDataDirectory, "settings.json");
    }

    /// <summary>
    /// Gets the plugin data directory.
    /// </summary>
    public string PluginDataDirectory
    {
        get
        {
            Directory.CreateDirectory(_pluginDataDirectory);
            return _pluginDataDirectory;
        }
    }

    /// <summary>
    /// Gets the plugin asset directory for large local model and runtime files.
    /// </summary>
    public string PluginAssetDirectory
    {
        get => LocalModelStorageService.ResolveAvailablePluginAssetDirectory(_settings?.Current, _pluginId);
    }

    /// <summary>
    /// Gets the active app process name.
    /// </summary>
    public string? ActiveAppProcessName => _activeWindow.GetActiveWindowProcessName();
    /// <summary>
    /// Gets the active app name.
    /// </summary>
    public string? ActiveAppName => _activeWindow.GetActiveWindowTitle();

    /// <summary>
    /// Gets the event bus.
    /// </summary>
    public IPluginEventBus EventBus => _eventBus;

    /// <summary>
    /// Gets the localization.
    /// </summary>
    public IPluginLocalization Localization => _localization;

    /// <summary>
    /// Gets the available profile names.
    /// </summary>
    public IReadOnlyList<string> AvailableProfileNames =>
        _workflows.Workflows.Select(w => w.Name).ToList();

    /// <summary>
    /// Gets whether live transcription preview windows should be shown.
    /// </summary>
    public bool LiveTranscriptionPreviewEnabled =>
        (_settings?.Current ?? AppSettings.Default).LiveTranscriptionEnabled;

    /// <summary>
    /// Gets the live transcription font size in device-independent pixels.
    /// </summary>
    public double LiveTranscriptionFontSize =>
        AppSettings.NormalizeLiveTranscriptionFontSize(
            (_settings?.Current ?? AppSettings.Default).LiveTranscriptionFontSize);

    /// <summary>
    /// Gets the preview bubble auto hide milliseconds.
    /// </summary>
    public int PreviewBubbleAutoHideMilliseconds =>
        AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            (_settings?.Current ?? AppSettings.Default).PreviewBubbleAutoHideMilliseconds);

    /// <summary>
    /// Performs log.
    /// </summary>
    public void Log(PluginLogLevel level, string message)
    {
        Debug.WriteLine($"[Plugin:{_pluginId}] [{level}] {message}");
    }

    /// <summary>
    /// Performs notify capabilities changed.
    /// </summary>
    public void NotifyCapabilitiesChanged()
    {
        Debug.WriteLine($"[Plugin:{_pluginId}] Capabilities changed, notifying host");
        _onCapabilitiesChanged?.Invoke();
    }

    #region Secrets (DPAPI-backed)

    /// <summary>
    /// Stores secret asynchronously..
    /// </summary>
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

    /// <summary>
    /// Performs load secret asynchronously.
    /// </summary>
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

    /// <summary>
    /// Deletes secret asynchronously.
    /// </summary>
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

    /// <summary>
    /// Reads a plugin setting from the JSON-backed plugin store and returns the default value when absent or invalid.
    /// </summary>
    /// <typeparam name="T">The expected setting value type.</typeparam>
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

    /// <summary>
    /// Performs key.
    /// </summary>
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
