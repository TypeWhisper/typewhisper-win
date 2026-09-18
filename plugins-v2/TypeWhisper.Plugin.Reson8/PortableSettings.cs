using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Reson8;
public sealed partial class Reson8Plugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
{
    async Task IApiKeyPlugin.SetApiKeyAsync(string apiKey)
    {
        var previous = _apiKey;
        try { await SetApiKeyAsync(apiKey); }
        catch { _apiKey = previous; throw; }
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured || !await ValidateApiKeyAsync(_apiKey!, ct))
            throw new InvalidOperationException("The API key could not be validated.");
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("baseUrl", "Server URL", "", _customBaseUrl), new("authHeader", "Authentication header", "", _customAuthHeader)];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || value.Contains('\r') || value.Contains('\n') || setting.Choices.Count > 0 && !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id) { case "baseUrl": SetCustomBaseUrl(value); break; case "authHeader": SetCustomAuthHeader(value); break; default: throw new ArgumentException("Unknown setting.", nameof(id)); }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("refresh", "Refresh custom models", "Uses the stored API key only when selected.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new InvalidOperationException("Store an API key first.");
        switch (id) { case "refresh": SetFetchedCustomModels(await FetchCustomModelsAsync(ct)); break; default: throw new ArgumentException("Unknown action.", nameof(id)); }
        await Task.CompletedTask;
        return "Updated.";
    }
}
