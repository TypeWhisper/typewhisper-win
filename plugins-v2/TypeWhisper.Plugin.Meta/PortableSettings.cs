using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Meta;

public sealed partial class MetaPlugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
{
    private string L(string en, string de) => Loc?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;

    Task IApiKeyPlugin.SetApiKeyAsync(string apiKey) => SetApiKeyAsync(apiKey);

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured)
            throw new InvalidOperationException(L("Save an API key first.", "Speichere zuerst einen API-Schlüssel."));
        await ValidateApiKeyAsync(_apiKey!, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("diarization", L("Speaker labels", "Sprecherkennzeichnung"),
            L("Identify speakers in live and recorded transcription.", "Kennzeichnet Sprecher bei Live-Transkription und aufgenommenem Audio."),
            _speakerDiarizationEnabled ? "true" : "false")
        {
            Section = PluginSettingsSection.Transcription,
            Choices = [new("false", L("Off", "Aus")), new("true", L("On", "Ein"))]
        },
        new("llmModel", L("Text model", "Textmodell"),
            L("Default model for text processing. Refresh models to load the catalog available to your account.",
                "Standardmodell für die Textverarbeitung. Aktualisiere die Modelle, um den Katalog deines Kontos zu laden."),
            _selectedLlmModelId ?? SupportedModels[0].Id)
        {
            Section = PluginSettingsSection.TextProcessing,
            Choices = SupportedModels.Select(model => new PluginSettingChoice(model.Id, model.DisplayName)).ToArray()
        },
        new("reasoning", L("Reasoning effort", "Denkaufwand"),
            L("Higher effort can improve complex tasks but increases response time.", "Ein höherer Denkaufwand kann komplexe Aufgaben verbessern, erhöht aber die Antwortzeit."), _reasoningEffort)
        {
            Section = PluginSettingsSection.TextProcessing,
            Choices = [new("minimal", L("Minimal", "Minimal")), new("low", L("Low", "Niedrig")),
                new("medium", L("Medium", "Mittel")), new("high", L("High", "Hoch")), new("xhigh", L("Very high", "Sehr hoch"))]
        }
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || value.Contains('\r') || value.Contains('\n') || !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id)
        {
            case "llmModel": SelectLlmModel(value); break;
            case "reasoning": SetReasoningEffort(value); break;
            case "diarization": SetSpeakerDiarizationEnabled(bool.Parse(value)); break;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [new("refresh", L("Refresh models", "Modelle aktualisieren"),
        L("Load transcription and text models using the saved API key.", "Lädt Transkriptions- und Textmodelle mit dem gespeicherten API-Schlüssel."))
        { Section = PluginSettingsSection.Connection }];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id != "refresh") throw new ArgumentException("Unknown action.", nameof(id));
        if (!IsConfigured) throw new InvalidOperationException(L("Save an API key first.", "Speichere zuerst einen API-Schlüssel."));
        var catalog = await RefreshAvailableModelsAsync(ct);
        if (catalog is null)
            throw new InvalidOperationException(L("Could not refresh models. The previous catalog was retained.", "Die Modelle konnten nicht aktualisiert werden. Der bisherige Katalog bleibt erhalten."));
        return L($"Models updated: {catalog.TranscriptionModels.Count} transcription, {catalog.LlmModels.Count} text.",
            $"Modelle aktualisiert: {catalog.TranscriptionModels.Count} Transkription, {catalog.LlmModels.Count} Text.");
    }
}
