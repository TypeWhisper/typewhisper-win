namespace TypeWhisper.WinUI;

internal sealed record DictationModelOption(string Id, string Name, bool Ready)
{
    /// <summary>IDs the provider used for this model before (<see cref="TypeWhisper.PluginSDK.Models.PluginModelInfo.FormerIds"/>).</summary>
    internal IReadOnlyList<string> FormerIds { get; init; } = [];
    /// <summary>Whether <paramref name="id"/> is this model's ID or one it had before.</summary>
    internal bool IsNamed(string id) => Id == id || FormerIds.Contains(id);
}

// Presentation snapshot: model IDs are scoped to their provider.
internal sealed record DictationProviderOption(string Id, string PluginId, string Name,
    bool Enabled, bool Configured, bool Cloud, string? SelectedModelId,
    IReadOnlyList<DictationModelOption> Models)
{
    /// <summary>Why machine policy refuses this provider for dictation, or null.</summary>
    internal string? Refusal { get; init; }
    internal bool Ready => Enabled && Configured && Models.Any(model => model.Ready);
    internal string Status => !Enabled ? Loc.T("Disabled") : !Configured ? Loc.T("Setup required")
        : !Ready ? Loc.T("Download required") : Loc.T("Ready");
    internal string? PreferredModelId => !Ready ? null
        : Models.FirstOrDefault(model => model.Id == SelectedModelId && model.Ready)?.Id
            ?? Models.First(model => model.Ready).Id;
}
