using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Services provided by the host application to plugins during activation.
/// </summary>
public interface IPluginHostServices
{
    /// <summary>Stores a secret value using DPAPI, scoped to the plugin.</summary>
    Task StoreSecretAsync(string key, string value);

    /// <summary>Loads a previously stored secret, or null if not found.</summary>
    Task<string?> LoadSecretAsync(string key);

    /// <summary>Deletes a stored secret.</summary>
    Task DeleteSecretAsync(string key);

    /// <summary>Gets a per-plugin setting value deserialized from JSON, or default if not found.</summary>
    T? GetSetting<T>(string key);

    /// <summary>Sets a per-plugin setting value (serialized to JSON).</summary>
    void SetSetting<T>(string key, T value);

    /// <summary>Directory where the plugin can store its own data files.</summary>
    string PluginDataDirectory { get; }

    /// <summary>Process name of the currently active foreground application, or null.</summary>
    string? ActiveAppProcessName { get; }

    /// <summary>Display name of the currently active foreground application, or null.</summary>
    string? ActiveAppName { get; }

    /// <summary>Event bus for publishing and subscribing to plugin events.</summary>
    IPluginEventBus EventBus { get; }

    /// <summary>Names of all available dictation profiles.</summary>
    IReadOnlyList<string> AvailableProfileNames { get; }

    /// <summary>Logs a message through the host logging system.</summary>
    void Log(PluginLogLevel level, string message);
}
