namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes a model available from a plugin provider.
/// </summary>
/// <param name="Id">Model identifier (e.g. "gpt-4o", "whisper-1").</param>
/// <param name="DisplayName">Human-readable name for the UI.</param>
public sealed record PluginModelInfo(string Id, string DisplayName);
