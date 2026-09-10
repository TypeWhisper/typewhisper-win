using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// Retains the existing profile's settings/secrets and forwards capability changes to its owner.
internal sealed class RuntimePluginHostServices(IPluginHostServices inner, Action changed) : IPluginHostServices
{
    public Task StoreSecretAsync(string key, string value) => inner.StoreSecretAsync(key, value);
    public Task<string?> LoadSecretAsync(string key) => inner.LoadSecretAsync(key);
    public Task DeleteSecretAsync(string key) => inner.DeleteSecretAsync(key);
    public T? GetSetting<T>(string key) => inner.GetSetting<T>(key);
    public void SetSetting<T>(string key, T value) => inner.SetSetting(key, value);
    public string PluginDataDirectory => inner.PluginDataDirectory;
    public string PluginAssetDirectory => inner.PluginAssetDirectory;
    public bool IsUiAutomation => inner.IsUiAutomation;
    public bool AllowLegacyDataMigration => inner.AllowLegacyDataMigration;
    public string? ActiveAppProcessName => inner.ActiveAppProcessName;
    public string? ActiveAppName => inner.ActiveAppName;
    public IPluginEventBus EventBus => inner.EventBus;
    public IReadOnlyList<string> AvailableProfileNames => inner.AvailableProfileNames;
    public void Log(PluginLogLevel level, string message) => inner.Log(level, message);
    public void NotifyCapabilitiesChanged() { try { inner.NotifyCapabilitiesChanged(); } finally { changed(); } }
    public IPluginLocalization Localization => inner.Localization;
    public void SetStreamingDisplayActive(bool active) => inner.SetStreamingDisplayActive(active);
}
