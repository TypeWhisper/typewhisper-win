using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cerebras;

public sealed partial class CerebrasPlugin
{
    private const string ProfileId = "cerebras";
    /// <inheritdoc />
    public string ProfileSelectorId => "configurationProfile";
    /// <inheritdoc />
    public string? AddProfileActionId => null;
    /// <inheritdoc />
    public string? RemoveProfileActionId => null;
    /// <inheritdoc />
    public bool ShowApiKeySettings => true;
    /// <inheritdoc />
    public string ConnectionIdentity => ProfileId;

    private string L(string en, string de)
    {
        try { return _host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en; }
        catch (NotSupportedException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en; }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(ProfileSelectorId, "Cerebras", "", ProfileId)
        { Section = PluginSettingsSection.Connection, Choices = [new(ProfileId, "Cerebras")] },
        new("selectedLlmModel", L("Default text model", "Standard-Textmodell"),
            L("Used when a workflow does not specify a model. Refresh to load models available to your account.",
              "Wird verwendet, wenn ein Workflow kein Modell vorgibt. Aktualisiere die Liste, um verfügbare Modelle für dein Konto zu laden."), SelectedModel)
        {
            Section = PluginSettingsSection.TextProcessing,
            Choices = (_draftModels ?? Models(_configuration)).Append(SelectedModel).Distinct(StringComparer.Ordinal)
                .Select(id => new PluginSettingChoice(id, DisplayName(id))).ToArray()
        },
        new("llmTemperatureMode", L("Temperature mode", "Temperaturmodus"),
            L("Use the provider default or set a custom sampling temperature.", "Anbietervorgabe oder eigene Temperatur verwenden."), _configuration.TemperatureMode)
        {
            Section = PluginSettingsSection.TextProcessing,
            Choices = [new("providerDefault", L("Provider default", "Anbietervorgabe")), new("custom", L("Custom", "Benutzerdefiniert"))]
        },
        new("llmTemperatureValue", L("Temperature", "Temperatur"),
            L("A value between 0 and 2. Model support varies.", "Ein Wert zwischen 0 und 2. Die Unterstützung hängt vom Modell ab."),
            _configuration.Temperature.ToString(CultureInfo.InvariantCulture), 16)
        { Section = PluginSettingsSection.TextProcessing, VisibleWhen = new("llmTemperatureMode", ["custom"]) }
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("checkConnection", L("Check connection", "Verbindung prüfen"),
            L("Test the entered key before saving.", "Den eingegebenen Key vor dem Speichern prüfen."))
        { Section = PluginSettingsSection.Connection },
        new("refreshModels", L("Refresh models", "Modelle aktualisieren"),
            L("Fetch available text models. Save settings to keep the list.", "Verfügbare Textmodelle laden. Zum Übernehmen die Einstellungen speichern."))
        { Section = PluginSettingsSection.TextProcessing }
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == ProfileSelectorId && value == ProfileId) return Task.CompletedTask;
        return SaveProfileSettingsAsync(ProfileId, new Dictionary<string, string> { [id] = value }, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values,
        string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != ProfileId) throw new ArgumentException("Unknown Cerebras configuration.");
        var next = _configuration;
        var fields = TextSettings.Where(f => f.Id != ProfileSelectorId).ToDictionary(f => f.Id);
        foreach (var (id, value) in values)
        {
            if (!fields.TryGetValue(id, out var field) || value.Length > field.MaxLength
                || field.Choices.Count > 0 && !field.Choices.Any(choice => choice.Value == value))
                throw new ArgumentException("Invalid Cerebras setting.");
            next = id switch
            {
                "selectedLlmModel" => next with { Model = value },
                "llmTemperatureMode" => next with { TemperatureMode = value },
                "llmTemperatureValue" => next with { Temperature = ParseTemperature(value) },
                _ => throw new ArgumentException("Unknown Cerebras setting.")
            };
        }
        // A catalog fetched with an entered key belongs to that key, not to a later edit.
        var effectiveKey = NormalizeKey(apiKey) ?? _apiKey;
        if (_draftModels is not null)
        {
            if (_draftModelsKey != effectiveKey) throw new ArgumentException("The API key changed after model refresh. Refresh models again.");
            next = next with { Models = _draftModels };
            if (!next.Models.Contains(next.Model, StringComparer.Ordinal)) next = next with { Model = next.Models[0] };
        }
        await CommitAsync(next, NormalizeKey(apiKey), cancellationToken);
        ClearDraft();
    }

    private static double ParseTemperature(string value) => double.TryParse(value.Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 && number <= 2
        ? number : throw new ArgumentException("Temperature must be between 0 and 2.");

    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        await CommitAsync(_configuration, NormalizeKey(apiKey), CancellationToken.None, replaceKey: true);
        ClearDraft();
    }

    private async Task CommitAsync(Configuration next, string? key, CancellationToken ct, bool replaceKey = false)
    {
        ct.ThrowIfCancellationRequested();
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        replaceKey |= key is not null;
        var oldSecret = _configuration.SecretName;
        var stagedSecret = replaceKey && key is not null ? "api-key-" + Guid.NewGuid().ToString("N") : null;
        try
        {
            if (stagedSecret is not null) await host.StoreSecretAsync(stagedSecret, key!);
            ct.ThrowIfCancellationRequested();
            if (replaceKey) next = next with { SecretName = stagedSecret };
            // One settings write commits the full configuration and the encrypted-key reference.
            host.SetSetting("configuration", next);
        }
        catch
        {
            if (stagedSecret is not null) await TryDeleteSecretAsync(host, stagedSecret);
            throw;
        }
        _configuration = next;
        if (replaceKey) _apiKey = key;
        host.NotifyCapabilitiesChanged();
        if (replaceKey && oldSecret is not null) await TryDeleteSecretAsync(host, oldSecret);
    }

    private static async Task TryDeleteSecretAsync(IPluginHostServices host, string name)
    {
        try { await host.DeleteSecretAsync(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { host.Log(PluginLogLevel.Warning, "An inactive encrypted Cerebras key could not be removed."); }
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken) =>
        (await ExecuteProfileActionAsync(ProfileId, id, new Dictionary<string, string>(), null, cancellationToken)).Message;

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId,
        IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != ProfileId) throw new ArgumentException("Unknown Cerebras configuration.");
        if (actionId is not ("checkConnection" or "refreshModels")) throw new ArgumentException("Unknown Cerebras action.");
        var key = NormalizeKey(apiKey) ?? _apiKey;
        var models = await FetchModelsAsync(key, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (actionId == "checkConnection") return new(L("Connection verified. No changes saved.", "Verbindung geprüft. Keine Änderungen gespeichert."));
        _draftModels = models;
        _draftModelsKey = key;
        return new(L($"{models.Length} text models loaded. Save settings to keep them.",
            $"{models.Length} Textmodelle geladen. Zum Übernehmen die Einstellungen speichern."), true);
    }
}
