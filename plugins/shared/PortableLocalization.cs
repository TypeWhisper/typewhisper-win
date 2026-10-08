using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Shared;
// The transcription worker and the vocabulary host have no localization and throw instead of returning null.
internal static class PortableLocalization
{
    internal static IPluginLocalization? TryGet(IPluginHostServices? host)
    {
        try { return host?.Localization; }
        catch (NotSupportedException) { return null; }
    }
}
