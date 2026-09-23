using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.SmallestAi;
public sealed partial class SmallestAiPlugin : IApiKeyPlugin
{
    Task IApiKeyPlugin.SetApiKeyAsync(string apiKey) => SetApiKeyAsync(apiKey);
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await RefreshVoicesAsync(ct);
    }
}
