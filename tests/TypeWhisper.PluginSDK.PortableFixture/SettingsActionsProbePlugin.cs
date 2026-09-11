namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>Configuration fixture that has actions without text settings.</summary>
public sealed class SettingsActionsProbePlugin : ITypeWhisperPlugin, IPluginSettingsActions
{
    /// <inheritdoc />
    public string PluginId => "test.typewhisper.runtime";
    /// <inheritdoc />
    public string PluginName => "Actions fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
    /// <inheritdoc />
    public void Dispose() { }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("connect", "Connect", "Connect the fixture")];
    /// <inheritdoc />
    public Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(id == "connect" ? "Connected" : throw new ArgumentException("Unknown action.", nameof(id)));
    }
}
