using System.Globalization;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Gemini;

public sealed partial class GeminiPlugin
{
    private sealed record LlmOptions(string? Model = null, string TemperatureMode = "providerDefault", double Temperature = 0.3);
    private LlmOptions _llmOptions = new();

    private string L(string en, string de)
    {
        try { return _host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en; }
        catch (NotSupportedException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en; }
    }

    private string SelectedLlmModel => SupportedModels.Any(m => m.Id == _llmOptions.Model)
        ? _llmOptions.Model! : SupportedModels[0].Id;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("selectedTranscriptionModel", L("Transcription model", "Transkriptionsmodell"),
            L("Use Refresh models to load the account's current catalog.", "Mit Modelle aktualisieren den aktuellen Katalog des Kontos laden."), _selectedTranscriptionModelId)
        { Section = PluginSettingsSection.Transcription, Choices = TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray() },
        new("transcriptionMode", L("Transcription mode", "Transkriptionsmodus"),
            L("Smart cleans up speech; Verbatim preserves the spoken wording.", "Smart bereinigt den Text; Wortgetreu erhält den gesprochenen Wortlaut."), FormatTranscriptionMode(_transcriptionMode))
        { Section = PluginSettingsSection.Transcription, Choices = [new("smart", "Smart"), new("verbatim", L("Verbatim", "Wortgetreu"))] },
        new("selectedLlmModel", L("Text model", "Textmodell"),
            L("Default for text processing. A workflow can choose another model.", "Standard für die Nachbearbeitung. Ein Workflow kann ein anderes Modell wählen."), SelectedLlmModel)
        { Section = PluginSettingsSection.TextProcessing, Choices = SupportedModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray() },
        new("llmTemperatureMode", L("Temperature", "Temperatur"),
            L("Use the provider default or set a custom temperature.", "Den Anbieterstandard oder eine eigene Temperatur verwenden."), _llmOptions.TemperatureMode)
        { Section = PluginSettingsSection.TextProcessing, Choices = [new("providerDefault", L("Provider default", "Anbieterstandard")), new("custom", L("Custom", "Benutzerdefiniert"))] },
        new("llmTemperatureValue", L("Custom temperature", "Eigene Temperatur"),
            L("A number from 0 to 2. Used only with Custom temperature.", "Eine Zahl von 0 bis 2. Wird nur bei benutzerdefinierter Temperatur verwendet."), _llmOptions.Temperature.ToString(CultureInfo.InvariantCulture))
        { Section = PluginSettingsSection.TextProcessing, VisibleWhen = new("llmTemperatureMode", ["custom"]) }
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("refreshModels", L("Refresh models", "Modelle aktualisieren"),
            L("Fetch and save transcription and text models using the stored API key.", "Transkriptions- und Textmodelle mit dem gespeicherten API-Key abrufen und speichern."))
        { Section = PluginSettingsSection.Connection }
    ];

    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken);
        try
        {
            var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
            cancellationToken.ThrowIfCancellationRequested();
            switch (id)
            {
                case "selectedTranscriptionModel": SelectModel(value); return;
                case "transcriptionMode" when value is "smart" or "verbatim":
                    SetTranscriptionMode(ParseTranscriptionMode(value)); host.NotifyCapabilitiesChanged(); return;
            }
            var next = id switch
            {
                "selectedLlmModel" when SupportedModels.Any(m => m.Id == value) => _llmOptions with { Model = value },
                "llmTemperatureMode" when value is "providerDefault" or "custom" => _llmOptions with { TemperatureMode = value },
                "llmTemperatureValue" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                    && double.IsFinite(temperature) && temperature is >= 0 and <= 2 => _llmOptions with { Temperature = temperature },
                _ => throw new ArgumentException("Invalid Gemini setting.")
            };
            host.SetSetting("llmOptions", next);
            _llmOptions = next;
            host.NotifyCapabilitiesChanged();
        }
        finally { _configurationGate.Release(); }
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new PluginRequestException("API key not configured", PluginRequestFailureKind.Configuration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = CreateNativeRequest(HttpMethod.Get, $"{NativeBaseUrl}/models?pageSize=1", _apiKey!);
        try
        {
            using var response = await TypeWhisper.PluginSDK.Helpers.OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, deadline.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new PluginRequestException("Gemini returned an invalid model catalog.", PluginRequestFailureKind.OutputIncomplete);
        }
        catch (JsonException ex)
        {
            throw new PluginRequestException("Gemini returned an invalid model catalog.", PluginRequestFailureKind.OutputIncomplete, innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new PluginRequestException("Gemini connection check timed out.", PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        if (id != "refreshModels") throw new ArgumentException("Unknown Gemini action.");
        var key = _apiKey;
        if (key is null) throw new PluginRequestException("API key not configured", PluginRequestFailureKind.Configuration);
        var catalog = await FetchModelCatalogAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog is null || catalog.LlmModels.Count + catalog.TranscriptionModels.Count == 0)
            throw new PluginRequestException("Gemini model discovery failed. The saved catalog is unchanged.", PluginRequestFailureKind.OutputIncomplete);
        if (!await SetFetchedModelCatalogAsync(catalog, key))
            throw new InvalidOperationException("The API key changed. Refresh the models again.");
        return L("Models updated.", "Modelle aktualisiert.");
    }
}
