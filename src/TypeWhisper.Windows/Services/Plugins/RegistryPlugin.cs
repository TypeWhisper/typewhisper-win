using System.Text.Json.Serialization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// A plugin entry from the remote plugin registry.
/// </summary>
public sealed record RegistryPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? MinHostVersion { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Category { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string? IconSystemName { get; init; }
    public bool RequiresApiKey { get; init; }
    public Dictionary<string, string>? Descriptions { get; init; }
}

/// <summary>
/// Installation state of a registry plugin relative to the local system.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginInstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Bundled
}
