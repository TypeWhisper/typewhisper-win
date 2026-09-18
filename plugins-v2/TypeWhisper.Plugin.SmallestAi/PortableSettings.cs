using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.SmallestAi;
public sealed partial class SmallestAiPlugin : IApiKeyPlugin
{
    async Task IApiKeyPlugin.SetApiKeyAsync(string apiKey)
    {
        var previous = _apiKey;
        try { await SetApiKeyAsync(apiKey); }
        catch { _apiKey = previous; throw; }
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured || !await ValidateApiKeyAsync(_apiKey!, ct))
            throw new InvalidOperationException("The API key could not be validated.");
    }
}
