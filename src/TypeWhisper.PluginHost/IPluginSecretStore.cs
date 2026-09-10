namespace TypeWhisper.PluginHost;

public interface IPluginSecretStore
{
    Task StoreAsync(string key, string value);
    Task<string?> LoadAsync(string key);
    Task DeleteAsync(string key);
}
