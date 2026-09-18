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
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("llmModel", "Text model", "", _selectedLlmModelId ?? ""), new("customVoice", "Custom voice ID", "", _customVoiceId), new("lowLatency", "Low-latency speech", "", _ttsLowLatency.ToString().ToLowerInvariant()) { Choices = [new("false", "false"), new("true", "true")] }, new("normalization", "Normalize spoken text", "", _ttsTextNormalization.ToString().ToLowerInvariant()) { Choices = [new("false", "false"), new("true", "true")] }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || value.Contains('\r') || value.Contains('\n') || setting.Choices.Count > 0 && !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id) { case "llmModel": SelectLlmModel(value); break; case "customVoice": SetCustomVoiceId(value); break; case "lowLatency": SetTtsLowLatency(bool.Parse(value)); break; case "normalization": SetTtsTextNormalization(bool.Parse(value)); break; default: throw new ArgumentException("Unknown setting.", nameof(id)); }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("models", "Refresh text models", "Uses the stored API key only when selected."), new("voices", "Refresh voices", "Uses the stored API key only when selected.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new InvalidOperationException("Store an API key first.");
        switch (id) { case "models": var models = await FetchLlmModelsAsync(ct); if (models.Count == 0) throw new InvalidOperationException("No models returned; previous catalog retained."); SetFetchedLlmModels(models); break; case "voices": var voices = await FetchVoicesAsync(ct); if (voices.Count == 0) throw new InvalidOperationException("No voices returned; previous catalog retained."); SetFetchedVoices(voices); break; default: throw new ArgumentException("Unknown action.", nameof(id)); }
        await Task.CompletedTask;
        return "Updated.";
    }
}
