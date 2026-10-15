namespace TypeWhisper.PluginSDK.Models;

/// <summary>Where a plugin processes the content it receives, declared by the manifest's <c>locality</c> field.</summary>
public enum PluginLocality
{
    /// <summary>Not declared or not recognized. Hosts treat it as not local under restrictions.</summary>
    Unknown,
    /// <summary>Processing stays on this device. No audio, transcript or prompt leaves it.</summary>
    Local,
    /// <summary>A fixed remote service of the plugin's vendor.</summary>
    Cloud,
    /// <summary>A user-defined endpoint, script or command whose destination the host cannot verify.</summary>
    Configurable
}

/// <summary>Parses the manifest's <c>locality</c> values: <c>local</c>, <c>cloud</c> and <c>configurable</c>.</summary>
public static class PluginLocalityValues
{
    /// <summary>The manifest value for on-device processing.</summary>
    public const string Local = "local";
    /// <summary>The manifest value for a vendor's remote service.</summary>
    public const string Cloud = "cloud";
    /// <summary>The manifest value for user-defined destinations.</summary>
    public const string Configurable = "configurable";

    /// <summary>Parses a manifest value; whitespace and case are ignored, anything else is <see cref="PluginLocality.Unknown"/>.</summary>
    public static PluginLocality Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Local => PluginLocality.Local,
        Cloud => PluginLocality.Cloud,
        Configurable => PluginLocality.Configurable,
        _ => PluginLocality.Unknown
    };
}
