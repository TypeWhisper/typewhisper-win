using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed partial class OpenRouterPlugin
{
    private string L(string en, string de)
    {
        try { return _host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en; }
        catch (NotSupportedException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en; }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(ProfileSelectorId, "OpenRouter", "", ProfileId)
        { Choices = [new(ProfileId, "OpenRouter")], Section = PluginSettingsSection.Connection },
        new(SelectedTranscriptionModelSettingName, L("Transcription model", "Transkriptionsmodell"),
            L("Default model for OpenRouter transcription.", "Standardmodell für OpenRouter-Transkriptionen."), _selectedTranscriptionModelId ?? DefaultTranscriptionModelId)
        {
            Section = PluginSettingsSection.Transcription,
            Choices = (_draftSpeechModels ?? _fetchedTranscriptionModels).Select(m => new PluginSettingChoice(m.Id, m.Name))
                .Concat(TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName))).DistinctBy(m => m.Value).ToArray()
        },
        new(SelectedLlmModelSettingName, L("Default text model", "Standard-Textmodell"),
            L("Used when a workflow does not specify a model. Prices are USD per million input/output tokens.",
              "Wird verwendet, wenn ein Workflow kein Modell vorgibt. Preise in USD pro Million Eingabe-/Ausgabetokens."),
            _selectedLlmModelId ?? DefaultLlmModelId)
        {
            Section = PluginSettingsSection.TextProcessing,
            Choices = (_draftTextModels ?? _fetchedModels).Select(m => new PluginSettingChoice(m.Id, $"{m.Name} · {m.FormattedPricing(L("Free", "Kostenlos"))}"))
                .Concat(SupportedModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName))).DistinctBy(m => m.Value).ToArray()
        },
        new(TemperatureModeSettingName, L("Temperature mode", "Temperaturmodus"),
            L("Use the provider default or set a custom sampling temperature.", "Anbietervorgabe oder eigene Temperatur verwenden."), _temperatureMode)
        {
            Section = PluginSettingsSection.TextProcessing,
            Choices = [new(TemperatureModeProviderDefault, L("Provider default", "Anbietervorgabe")), new(TemperatureModeCustom, L("Custom", "Benutzerdefiniert"))]
        },
        new(TemperatureValueSettingName, L("Temperature", "Temperatur"),
            L("A value between 0 and 2. Model support varies.", "Ein Wert zwischen 0 und 2. Die Unterstützung hängt vom Modell ab."),
            _temperatureValue.ToString(CultureInfo.InvariantCulture), 16)
        {
            Section = PluginSettingsSection.TextProcessing,
            VisibleWhen = new(TemperatureModeSettingName, [TemperatureModeCustom])
        }
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        switch (id)
        {
            case SelectedLlmModelSettingName when SupportedModels.Any(m => m.Id == value):
                SelectLlmModel(value); break;
            case TemperatureModeSettingName when value is TemperatureModeProviderDefault or TemperatureModeCustom:
                SetTemperatureMode(value); break;
            case TemperatureValueSettingName when double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number) && number >= 0 && number <= 2:
                SetTemperatureValue(number); break;
            default: throw new ArgumentException("Unsupported OpenRouter setting value.");
        }
        _host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("checkConnection", L("Check connection", "Verbindung prüfen"), L("Test the entered key before saving.", "Den eingegebenen Key vor dem Speichern prüfen."))
            { Section = PluginSettingsSection.Connection },
        new("refreshModels", L("Refresh models", "Modelle aktualisieren"), L("Load text and transcription models. Save to keep the catalog.", "Text- und Transkriptionsmodelle laden. Zum Übernehmen speichern."))
            { Section = PluginSettingsSection.Transcription },
        new("checkBudget", L("Check key budget", "Key-Budget prüfen"), L("Check this key's spending limit.", "Ausgabelimit dieses Keys prüfen."))
            { Section = PluginSettingsSection.Connection }
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        switch (id)
        {
            case "refreshTextModels":
                var text = await FetchModelsAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (text.Count == 0) throw new InvalidOperationException(L("Could not refresh text models. The saved list is unchanged.", "Textmodelle konnten nicht aktualisiert werden. Die gespeicherte Liste bleibt erhalten."));
                SetFetchedModels(text);
                return L($"{text.Count} text models loaded.", $"{text.Count} Textmodelle geladen.");
            case "refreshTranscriptionModels":
                var speech = await FetchTranscriptionModelsAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (speech.Count == 0) throw new InvalidOperationException(L("Could not refresh transcription models. The saved list is unchanged.", "Transkriptionsmodelle konnten nicht aktualisiert werden. Die gespeicherte Liste bleibt erhalten."));
                SetFetchedTranscriptionModels(speech);
                return L($"{speech.Count} transcription models loaded.", $"{speech.Count} Transkriptionsmodelle geladen.");
            case "checkBudget":
                using (var key = await ReadKeyInfoAsync(cancellationToken))
                {
                    var data = key.RootElement.GetProperty("data");
                    if (TryReadDouble(data, "limit_remaining", out var remaining))
                        return L($"Key budget remaining: ${remaining.ToString("0.00", CultureInfo.InvariantCulture)}.", $"Verbleibendes Key-Budget: {remaining.ToString("0.00", CultureInfo.GetCultureInfo("de-DE"))} USD.");
                    return L("This key has no reported spending limit. This is not the account balance.", "Für diesen Key wird kein Ausgabelimit gemeldet. Das ist keine Angabe zum Kontoguthaben.");
                }
            default: throw new ArgumentException("Unknown OpenRouter settings action.", nameof(id));
        }
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var _ = await ReadKeyInfoAsync(ct);
    }

    private async Task<JsonDocument> ReadKeyInfoAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new PluginRequestException("API key not configured.", PluginRequestFailureKind.Configuration);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var doc = ParseResponse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new PluginRequestException("OpenRouter returned invalid key information.", PluginRequestFailureKind.OutputIncomplete);
        }
        return doc;
    }
}
