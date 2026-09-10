namespace TypeWhisper.Core.Models;

/// <summary>Minimal execution provenance without transcript copies or provider error details.</summary>
public sealed record TextProcessorProvenance(string PluginId, string Version, string Status);
