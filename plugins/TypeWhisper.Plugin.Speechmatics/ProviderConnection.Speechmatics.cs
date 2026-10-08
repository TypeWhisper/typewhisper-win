using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared;

// Speechmatics part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    // Speechmatics expects lower-case language codes.
    private static partial string NormalizeLanguage(string language) => language.ToLowerInvariant();
    private static partial IEnumerable<string> ParseTerms(string? prompt) => PluginDictionaryTerms.ParsePrompt(prompt);
}
