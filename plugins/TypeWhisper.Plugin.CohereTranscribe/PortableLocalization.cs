using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.CohereTranscribe;
internal static class PortableLocalization
{
    internal static IPluginLocalization? TryGet(IPluginHostServices? host)
    {
        try { return host?.Localization; }
        catch (NotSupportedException) { return null; }
    }
}
