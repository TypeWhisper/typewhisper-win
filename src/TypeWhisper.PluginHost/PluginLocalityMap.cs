using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>Maps a manifest's or catalog entry's declared locality to the policy's content locality.</summary>
public static class PluginLocalityMap
{
    public static ContentLocality ToContentLocality(PluginLocality locality) => locality switch
    {
        PluginLocality.Local => ContentLocality.Local,
        PluginLocality.Cloud => ContentLocality.Cloud,
        PluginLocality.Configurable => ContentLocality.Configurable,
        _ => ContentLocality.Unknown
    };
}
