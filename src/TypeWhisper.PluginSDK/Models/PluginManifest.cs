namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes a plugin's metadata, loaded from plugin.json in the plugin directory.
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

    /// <summary>DLL file name containing the plugin type (e.g. "MyPlugin.dll").</summary>
    public required string AssemblyName { get; init; }

    /// <summary>Fully-qualified class name implementing ITypeWhisperPlugin.</summary>
    public required string PluginClass { get; init; }
}
