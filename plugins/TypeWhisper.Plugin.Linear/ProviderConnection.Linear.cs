namespace TypeWhisper.Plugin.Shared;

// Linear part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    private static partial string NormalizeLanguage(string language) => language;
    private static partial IEnumerable<string> ParseTerms(string? prompt) =>
        prompt?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
