namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Provides non-sensitive plugin configuration used only by isolated UI automation runs.
/// </summary>
internal static class UiAutomationPluginFixtures
{
    private const string PlaceholderSecret = "typewhisper-ui-automation-placeholder";
    private const string PlaceholderAccountId = "0123456789abcdef0123456789abcdef";

    private static readonly HashSet<(string PluginId, string SecretName)> ConfiguredSecrets = new()
    {
        ("com.typewhisper.assemblyai", "api-key"),
        ("com.typewhisper.cerebras", "api-key"),
        ("com.typewhisper.claude", "api-key"),
        ("com.typewhisper.cloudflare-asr", "account-id"),
        ("com.typewhisper.cloudflare-asr", "api-token"),
        ("com.typewhisper.cohere", "apiKey"),
        ("com.typewhisper.deepgram", "api-key"),
        ("com.typewhisper.elevenlabs", "api-key"),
        ("com.typewhisper.fireworks", "apiKey"),
        ("com.typewhisper.gemini", "api-key"),
        ("com.typewhisper.gladia", "api-key"),
        ("com.typewhisper.google-cloud-stt", "api-key"),
        ("com.typewhisper.groq", "api-key"),
        ("com.typewhisper.linear", "api-key"),
        ("com.typewhisper.meta", "api-key"),
        ("com.typewhisper.openai", "api-key"),
        ("com.typewhisper.openai-compatible", "api-key"),
        ("com.typewhisper.openai-vector-memory", "api-key"),
        ("com.typewhisper.openrouter", "api-key"),
        ("com.typewhisper.qwen3-stt", "api-key"),
        ("com.typewhisper.reson8", "api-key"),
        ("com.typewhisper.smallest-ai", "api-key"),
        ("com.typewhisper.soniox", "api-key"),
        ("com.typewhisper.speechmatics", "api-key"),
        ("com.typewhisper.voxtral", "api-key"),
        ("com.typewhisper.xai", "api-key")
    };

    /// <summary>
    /// Gets a safe placeholder secret for a supported screenshot fixture.
    /// </summary>
    public static bool TryGetSecret(string pluginId, string key, out string? value)
    {
        if (ConfiguredSecrets.Contains((pluginId, key)))
        {
            value = string.Equals(key, "account-id", StringComparison.Ordinal)
                ? PlaceholderAccountId
                : PlaceholderSecret;
            return true;
        }

        value = null;
        return false;
    }
}
