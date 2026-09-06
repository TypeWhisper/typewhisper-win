namespace TypeWhisper.WinUI;

internal sealed record DictationModelOption(string Id, string Name, bool Ready);

// Presentation snapshot: model IDs are scoped to their provider.
internal sealed record DictationProviderOption(string Id, string PluginId, string Name,
    bool Enabled, bool Configured, bool Cloud, string? SelectedModelId,
    IReadOnlyList<DictationModelOption> Models)
{
    internal bool Ready => Enabled && Configured && Models.Any(model => model.Ready);
    internal string Status => !Enabled ? "Disabled" : !Configured ? "Setup required"
        : !Ready ? "Download required" : "Ready";
    internal string? PreferredModelId => !Ready ? null
        : Models.FirstOrDefault(model => model.Id == SelectedModelId && model.Ready)?.Id
            ?? Models.First(model => model.Ready).Id;
}
