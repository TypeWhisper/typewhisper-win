namespace TypeWhisper.PluginSDK;

/// <summary>A provider configured by an API key through host-rendered settings.</summary>
public interface IApiKeyPlugin : ITypeWhisperPlugin
{
    /// <summary>Whether a key has been stored; the key itself is never exposed to the UI.</summary>
    bool IsConfigured { get; }

    /// <summary>Stores a key, or removes it when empty.</summary>
    Task SetApiKeyAsync(string apiKey);

    /// <summary>Checks the stored key against the provider without uploading audio.</summary>
    Task ValidateConfigurationAsync(CancellationToken ct);
}
