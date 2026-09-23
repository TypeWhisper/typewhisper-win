using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Linear;

/// <summary>Independent portable Linear provider, compared with Windows 4db8f6ac and macOS ac00e39e.</summary>
public sealed partial class LinearPlugin : IActionPlugin, IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
{
    private readonly ProviderConnection Connection;
    /// <summary>Creates a provider with isolated HTTP transport.</summary>
    public LinearPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) }) { }
    internal LinearPlugin(HttpClient http) => Connection = new(http);
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.linear";
    /// <inheritdoc />
    public string PluginName => "Linear";
    /// <inheritdoc />
    public string PluginVersion => "1.2.2";
    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        await Connection.ActivateAsync(host);
        _catalog = host.GetSetting<SelectionCatalog>("selectionCatalog");
    }
    /// <inheritdoc />
    public Task DeactivateAsync() { Connection.Deactivate(); _catalog = null; return Task.CompletedTask; }
    /// <inheritdoc />
    public bool IsConfigured => Connection.Configured;
    /// <inheritdoc />
    public Task SetApiKeyAsync(string apiKey) => Connection.SetKeyAsync(apiKey);
    /// <inheritdoc />
    public void Dispose() => Connection.Dispose();

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = TextSettings.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("Unknown setting.");
        value = value.Trim();
        if (value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid setting value.");
        ValidateValue(id, value);
        return Connection.SaveAsync(id, value, cancellationToken);
    }
    private void ValidateValue(string id, string value)
    {
        if (id is "teamId" or "projectId" && value.Length != 0 && !Guid.TryParse(value, out _))
            throw new ArgumentException(Connection.L("Select a valid team or project.", "Wähle ein gültiges Team oder Projekt."));
    }
}
