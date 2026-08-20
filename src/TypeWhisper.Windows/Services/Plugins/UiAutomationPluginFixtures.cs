namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Provides non-sensitive plugin configuration used only by isolated UI automation runs.
/// </summary>
internal static class UiAutomationPluginFixtures
{
    private const string ApiKeySecretName = "api-key";
    private const string PlaceholderApiKey = "typewhisper-ui-automation-placeholder";

    private static readonly HashSet<string> ApiKeyPluginIds = new(StringComparer.Ordinal)
    {
        "com.typewhisper.elevenlabs",
        "com.typewhisper.openai",
        "com.typewhisper.openrouter",
        "com.typewhisper.reson8",
        "com.typewhisper.smallest-ai",
        "com.typewhisper.soniox",
        "com.typewhisper.xai"
    };

    /// <summary>
    /// Gets a safe placeholder secret for a supported screenshot fixture.
    /// </summary>
    public static bool TryGetSecret(string pluginId, string key, out string? value)
    {
        if (string.Equals(key, ApiKeySecretName, StringComparison.Ordinal)
            && ApiKeyPluginIds.Contains(pluginId))
        {
            value = PlaceholderApiKey;
            return true;
        }

        value = null;
        return false;
    }
}
