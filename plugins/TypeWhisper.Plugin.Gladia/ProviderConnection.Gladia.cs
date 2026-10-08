using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared;

// Gladia part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    private static partial string NormalizeLanguage(string language) => language;
    private static partial IEnumerable<string> ParseTerms(string? prompt) => PluginDictionaryTerms.ParsePrompt(prompt);
}
