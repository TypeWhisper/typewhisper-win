using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed partial class OpenRouterPlugin
{
    private const string ProfileId = "openrouter";
    private string? _activeSecretName = ApiKeySecretName;
    private List<OpenRouterFetchedModel>? _draftTextModels;
    private List<OpenRouterFetchedModel>? _draftSpeechModels;

    private sealed record Configuration(string? SpeechModel, string? TextModel, bool UserSelectedTextModel,
        string TemperatureMode, double Temperature, List<OpenRouterFetchedModel> SpeechModels,
        List<OpenRouterFetchedModel> TextModels, string? SecretName);

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

    private Configuration CaptureConfiguration() => new(_selectedTranscriptionModelId, _selectedLlmModelId,
        _hasUserSelectedLlmModel, _temperatureMode, _temperatureValue, _fetchedTranscriptionModels, _fetchedModels, _activeSecretName);

    private void ApplyConfiguration(Configuration value)
    {
        _selectedTranscriptionModelId = value.SpeechModel;
        _selectedLlmModelId = value.TextModel;
        _hasUserSelectedLlmModel = value.UserSelectedTextModel;
        _temperatureMode = NormalizeTemperatureMode(value.TemperatureMode);
        _temperatureValue = NormalizeTemperatureValue(value.Temperature);
        _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(value.SpeechModels);
        _fetchedModels = NormalizeFetchedModels(value.TextModels);
        _activeSecretName = value.SecretName;
    }

    private void CommitConfiguration(Configuration value, bool notify = true)
    {
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        _host.SetSetting("configuration", value);
        ApplyConfiguration(value);
        if (notify) _host.NotifyCapabilitiesChanged();
    }

    private async Task CommitWithKeyAsync(Configuration value, string? replacementKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        if (replacementKey is null) { CommitConfiguration(value); return; }
        var normalized = NormalizeApiKey(replacementKey);
        var oldSecret = _activeSecretName;
        var stagedSecret = normalized is null ? null : "api-key-" + Guid.NewGuid().ToString("N");
        try
        {
            if (stagedSecret is not null) await host.StoreSecretAsync(stagedSecret, normalized!);
            ct.ThrowIfCancellationRequested();
            // The single settings write is the commit point for all fields and the key reference.
            CommitConfiguration(value with { SecretName = stagedSecret }, notify: false);
        }
        catch
        {
            if (stagedSecret is not null) await TryDeleteSecretAsync(stagedSecret);
            throw;
        }
        _apiKey = normalized;
        host.NotifyCapabilitiesChanged();
        if (oldSecret is not null) await TryDeleteSecretAsync(oldSecret);
    }

    private async Task TryDeleteSecretAsync(string name)
    {
        try { if (_host is not null) await _host.DeleteSecretAsync(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _host?.Log(PluginLogLevel.Warning, "An inactive encrypted key could not be removed."); }
    }

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values,
        string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != ProfileId) throw new ArgumentException("Unknown OpenRouter configuration.");
        var fields = TextSettings.Where(f => f.Id != ProfileSelectorId).ToDictionary(f => f.Id);
        var next = CaptureConfiguration();
        foreach (var (id, value) in values)
        {
            if (!fields.TryGetValue(id, out var field) || value.Length > field.MaxLength
                || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
                throw new ArgumentException("Invalid OpenRouter setting.");
            next = id switch
            {
                SelectedTranscriptionModelSettingName => next with { SpeechModel = value },
                SelectedLlmModelSettingName => next with { TextModel = value, UserSelectedTextModel = true },
                TemperatureModeSettingName => next with { TemperatureMode = value },
                TemperatureValueSettingName => next with { Temperature = ParseTemperature(value) },
                _ => throw new ArgumentException("Unknown OpenRouter setting.")
            };
        }
        next = next with { TextModels = _draftTextModels ?? next.TextModels, SpeechModels = _draftSpeechModels ?? next.SpeechModels };
        await CommitWithKeyAsync(next, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, cancellationToken);
        _draftTextModels = _draftSpeechModels = null;
    }

    private static double ParseTemperature(string value) =>
        double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 && number <= 2
            ? number : throw new ArgumentException("Temperature must be between 0 and 2.");

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId,
        IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != ProfileId) throw new ArgumentException("Unknown OpenRouter configuration.");
        // A separate provider instance borrows the transport and cannot mutate the active connection.
        var draft = new OpenRouterPlugin(_httpClient) { _apiKey = NormalizeApiKey(apiKey) ?? _apiKey };
        switch (actionId)
        {
            case "checkConnection":
                await draft.ValidateConfigurationAsync(cancellationToken);
                return new(L("Connection verified. No changes saved.", "Verbindung geprüft. Keine Änderungen gespeichert."));
            case "refreshModels":
                var text = await draft.FetchModelsAsync(cancellationToken);
                var speech = await draft.FetchTranscriptionModelsAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (text.Count == 0 || speech.Count == 0) throw new InvalidOperationException("Could not fetch both model catalogs. Retry the refresh.");
                _draftTextModels = text;
                _draftSpeechModels = speech;
                return new(L($"{text.Count} text models and {speech.Count} transcription models loaded. Save to keep them.",
                    $"{text.Count} Textmodelle und {speech.Count} Transkriptionsmodelle geladen. Zum Übernehmen speichern."), true);
            case "checkBudget":
                using (var info = await draft.ReadKeyInfoAsync(cancellationToken))
                {
                    var data = info.RootElement.GetProperty("data");
                    return new(TryReadDouble(data, "limit_remaining", out var remaining)
                        ? L($"Key budget remaining: {remaining:0.00} USD.", $"Verbleibendes Key-Budget: {remaining:0.00} USD.")
                        : L("No key spending limit reported.", "Kein Ausgabelimit für diesen Key gemeldet."));
                }
            default: throw new ArgumentException("Unknown OpenRouter action.");
        }
    }
}
