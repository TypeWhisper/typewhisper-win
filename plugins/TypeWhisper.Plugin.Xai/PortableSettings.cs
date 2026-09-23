using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Xai;
public sealed partial class XaiPlugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
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
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("llmModel", L("Text model", "Textmodell"),
            L("Refresh models to load the models available to your account.", "Aktualisiere die Modelle, um die verfügbaren Modelle deines Kontos zu laden."), _selectedLlmModelId ?? DefaultLlmModelId)
        { Section = PluginSettingsSection.TextProcessing, Choices = SupportedModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray() },
        new("voice", L("Voice", "Stimme"), L("Default voice for spoken feedback.", "Standardstimme für die Sprachausgabe."), _selectedVoiceId ?? XaiTtsConfiguration.DefaultVoiceId)
        { Section = PluginSettingsSection.Speech, Choices = AvailableVoices.Select(v => new PluginSettingChoice(v.Id, v.DisplayName)).ToArray() },
        new("customVoice", L("Custom voice ID (optional)", "Eigene Stimmen-ID (optional)"),
            L("Overrides the selected voice. Leave empty to use a voice from the list.", "Ersetzt die ausgewählte Stimme. Leer lassen, um eine Stimme aus der Liste zu verwenden."), _customVoiceId, 200)
        { Section = PluginSettingsSection.Speech },
        new("lowLatency", L("Low-latency speech", "Sprachausgabe mit niedriger Latenz"),
            L("Requests latency optimization from xAI, with a possible quality trade-off.", "Fordert bei xAI eine Latenzoptimierung an, gegebenenfalls auf Kosten der Qualität."), _ttsLowLatency.ToString().ToLowerInvariant())
        { Section = PluginSettingsSection.Speech, Choices = BooleanChoices },
        new("normalization", L("Normalize spoken text", "Gesprochenen Text normalisieren"),
            L("Expands numbers, abbreviations and symbols before speech synthesis.", "Wandelt Zahlen, Abkürzungen und Symbole vor der Sprachausgabe in gesprochene Formen um."), _ttsTextNormalization.ToString().ToLowerInvariant())
        { Section = PluginSettingsSection.Speech, Choices = BooleanChoices }
    ];
    private string L(string en, string de) => Loc?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;
    private IReadOnlyList<PluginSettingChoice> BooleanChoices => [new("false", L("Off", "Aus")), new("true", L("On", "Ein"))];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || value.Contains('\r') || value.Contains('\n') || setting.Choices.Count > 0 && !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id) { case "llmModel": SelectLlmModel(value); break; case "voice": SelectVoice(value); break; case "customVoice": SetCustomVoiceId(value); break; case "lowLatency": SetTtsLowLatency(bool.Parse(value)); break; case "normalization": SetTtsTextNormalization(bool.Parse(value)); break; default: throw new ArgumentException("Unknown setting.", nameof(id)); }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("models", L("Refresh text models", "Textmodelle aktualisieren"), L("Load text models available to your account.", "Lädt die verfügbaren Textmodelle deines Kontos.")), new("voices", L("Refresh voices", "Stimmen aktualisieren"), L("Load available voices.", "Lädt die verfügbaren Stimmen."))];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new InvalidOperationException("Store an API key first.");
        switch (id) { case "models": var models = await FetchLlmModelsAsync(ct); if (models.Count == 0) throw new InvalidOperationException("No models returned; previous catalog retained."); SetFetchedLlmModels(models); break; case "voices": var voices = await FetchVoicesAsync(ct); if (voices.Count == 0) throw new InvalidOperationException("No voices returned; previous catalog retained."); SetFetchedVoices(voices); break; default: throw new ArgumentException("Unknown action.", nameof(id)); }
        await Task.CompletedTask;
        return L("Updated.", "Aktualisiert.");
    }
}
