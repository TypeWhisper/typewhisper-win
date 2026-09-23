using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Reson8;
public sealed partial class Reson8Plugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
{
    Task IApiKeyPlugin.SetApiKeyAsync(string apiKey) => SetApiKeyAsync(apiKey);
    private string L(string en, string de) => Loc?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured || !await ValidateApiKeyAsync(_apiKey!, ct))
            throw new InvalidOperationException("The API key could not be validated.");
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("baseUrl", L("Server URL", "Server-URL"), L("Keep the default unless using a compatible server.", "Behalte den Standard bei, sofern du keinen kompatiblen Server verwendest."), _customBaseUrl) { Section = PluginSettingsSection.Connection },
        new("authHeader", L("Authentication header", "Authentifizierungsheader"), L("Use Authorization for Reson8.", "Verwende Authorization für Reson8."), _customAuthHeader) { Section = PluginSettingsSection.Connection }
    ];
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
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("refresh", L("Refresh custom models", "Eigene Modelle aktualisieren"), L("Fetches custom models available to this API key.", "Lädt die eigenen Modelle, die mit diesem API-Schlüssel verfügbar sind."))];
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
