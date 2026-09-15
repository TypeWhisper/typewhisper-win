using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

internal sealed class TestHost(string directory) : IPluginHostServices
{
    private readonly Dictionary<string, object?> _settings = [];
    public string PluginDataDirectory => directory;
    public string? ActiveAppProcessName => null;
    public string? ActiveAppName => null;
    public IReadOnlyList<string> AvailableProfileNames => [];
    public IPluginEventBus EventBus => throw new NotSupportedException();
    public IPluginLocalization Localization => throw new NotSupportedException();
    public Task StoreSecretAsync(string key, string value) => throw new NotSupportedException();
    public Task<string?> LoadSecretAsync(string key) => throw new NotSupportedException();
    public Task DeleteSecretAsync(string key) => throw new NotSupportedException();
    public T? GetSetting<T>(string key) => _settings.TryGetValue(key, out var value) ? (T?)value : default;
    public void SetSetting<T>(string key, T value) => _settings[key] = value;
    public void Log(PluginLogLevel level, string message) { }
    public void NotifyCapabilitiesChanged() { }
}
