using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed record CliProfile
{
    public string Id { get; init; } = "codex";
    public string Name { get; init; } = "Codex CLI";
    public string Provider { get; init; } = "codex";
    public string Executable { get; init; } = "";
    public string Model { get; init; } = "default";
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PluginModelInfo> Models { get; init; } = [];
    public OpenCodeProfileCatalogCache? OpenCodeCatalog { get; init; }

    internal CliProviderDescriptor Descriptor => CliProviderDescriptor.All.Single(d => d.Key == Provider)
        .ForProfile(Id, Name, Environment);
}
