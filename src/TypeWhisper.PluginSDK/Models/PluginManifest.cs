using System.Text.Json.Serialization;

namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes a plugin's metadata, loaded from manifest.json in the plugin directory.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Unique plugin identifier (e.g. "com.typewhisper.openai").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable plugin name.</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version (e.g. "1.0.0").</summary>
    public required string Version { get; init; }

    /// <summary>Minimum host version required, or null for any.</summary>
    public string? MinHostVersion { get; init; }

    /// <summary>Plugin author name.</summary>
    public string? Author { get; init; }

    /// <summary>Short description of the plugin.</summary>
    public string? Description { get; init; }


    /// <summary>Category identifiers for plugins that expose multiple capabilities.</summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>A single category identifier, as manifests declared it before <see cref="Categories"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    /// <summary>
    /// The declared categories, falling back to <see cref="Category"/>, with blank entries removed and the names older
    /// manifests used mapped to the current ones. Null when the manifest declares none.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string>? ResolvedCategories
    {
        get
        {
            var declared = Categories ?? (Category is null ? null : new[] { Category });
            return declared?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim() switch
            {
                "integration" => "integrations",
                "post-processor" => "post-processing",
                var other => other
            }).ToArray();
        }
    }

    /// <summary>Whether this is a local (on-device) or cloud-based plugin. Kept for hosts that predate <see cref="Locality"/>.</summary>
    public bool IsLocal { get; init; }

    /// <summary>
    /// Where the plugin processes content: <c>local</c>, <c>cloud</c> or <c>configurable</c> (user-defined endpoints,
    /// scripts or commands). A missing value derives from <see cref="IsLocal"/>.
    /// </summary>
    public string? Locality { get; init; }

    /// <summary>
    /// The declared locality, or for a manifest without the field the value implied by <see cref="IsLocal"/>; an
    /// explicit blank or unrecognized value is <see cref="PluginLocality.Unknown"/>.
    /// </summary>
    [JsonIgnore]
    public PluginLocality ResolvedLocality => Locality is null
        ? IsLocal ? PluginLocality.Local : PluginLocality.Cloud
        : PluginLocalityValues.Parse(Locality);

    /// <summary>Package IDs required under Dependencies; they are shipped and removed with the parent.</summary>
    public IReadOnlyList<string> BundledDependencies { get; init; } = [];

    /// <summary>Internal packages are not bootstrapped as independently installed integrations.</summary>
    public bool IsInternalDependency { get; init; }

    /// <summary>DLL file name containing the plugin type (e.g. "MyPlugin.dll").</summary>
    public required string AssemblyName { get; init; }

    /// <summary>Fully-qualified class name implementing ITypeWhisperPlugin.</summary>
    public required string PluginClass { get; init; }
}
