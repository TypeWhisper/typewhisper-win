using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.GraniteSpeech;
internal static class PortableLocalization
{
    internal static IPluginLocalization? TryGet(IPluginHostServices? host)
    {
        try { return host?.Localization; }
        catch (NotSupportedException) { return null; }
    }
}
