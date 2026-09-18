using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Meta;
public sealed partial class MetaPlugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
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
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("llmModel", "Text model", "", _selectedLlmModelId ?? ""), new("reasoning", "Reasoning effort", "", _reasoningEffort) { Choices = [new("low", "low"), new("medium", "medium"), new("high", "high")] }, new("diarization", "Speaker diarization", "", _speakerDiarizationEnabled.ToString().ToLowerInvariant()) { Choices = [new("false", "false"), new("true", "true")] }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || value.Contains('\r') || value.Contains('\n') || setting.Choices.Count > 0 && !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id) { case "llmModel": SelectLlmModel(value); break; case "reasoning": SetReasoningEffort(value); break; case "diarization": SetSpeakerDiarizationEnabled(bool.Parse(value)); break; default: throw new ArgumentException("Unknown setting.", nameof(id)); }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("refresh", "Refresh models", "Uses the stored API key only when selected.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new InvalidOperationException("Store an API key first.");
        switch (id) { case "refresh": if (await RefreshAvailableModelsAsync(ct) is null) throw new InvalidOperationException("Model refresh failed; the previous catalog was retained."); break; default: throw new ArgumentException("Unknown action.", nameof(id)); }
        await Task.CompletedTask;
        return "Updated.";
    }
}
