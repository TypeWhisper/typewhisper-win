using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed partial class OpenRouterPlugin
{
    private const string ProfileId = "openrouter";
    private string? _activeSecretName = ApiKeySecretName;
    private List<OpenRouterFetchedModel>? _draftTextModels;
    private List<OpenRouterFetchedModel>? _draftSpeechModels;
    private DateTimeOffset? _draftFetchedAt;

    // SpeechModel and TextModel hold the saved choices, which a list that lacks them does not overwrite.
    // The fetch dates sit next to their lists, so one write keeps both consistent.
    private sealed record Configuration(string? SpeechModel, string? TextModel, bool UserSelectedTextModel,
        string TemperatureMode, double Temperature, List<OpenRouterFetchedModel> SpeechModels,
        List<OpenRouterFetchedModel> TextModels, string? SecretName,
        [property: JsonConverter(typeof(FetchDateConverter))] DateTimeOffset? SpeechModelsFetchedAt = null,
        [property: JsonConverter(typeof(FetchDateConverter))] DateTimeOffset? TextModelsFetchedAt = null);

    /// <summary>
    /// Reads a damaged fetch date as "no date", so the list refreshes instead of the whole configuration
    /// failing to load.
    /// </summary>
    private sealed class FetchDateConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value))
                return value.ToUniversalTime();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value is { } fetchedAt)
                writer.WriteStringValue(fetchedAt.ToUniversalTime());
            else
                writer.WriteNullValue();
        }
    }

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

    private Configuration CaptureConfiguration() => new(_preferredTranscriptionModelId, _preferredLlmModelId,
        _hasUserSelectedLlmModel, _temperatureMode, _temperatureValue, _fetchedTranscriptionModels, _fetchedModels, _activeSecretName,
        _speechModelsFetchedAt, _textModelsFetchedAt);

    private void ApplyConfiguration(Configuration value)
    {
        _preferredTranscriptionModelId = value.SpeechModel;
        _preferredLlmModelId = value.TextModel;
        _hasUserSelectedLlmModel = value.UserSelectedTextModel;
        _temperatureMode = NormalizeTemperatureMode(value.TemperatureMode);
        _temperatureValue = NormalizeTemperatureValue(value.Temperature);
        _fetchedTranscriptionModels = NormalizeFetchedTranscriptionModels(value.SpeechModels);
        _fetchedModels = NormalizeFetchedModels(value.TextModels);
        _speechModelsFetchedAt = value.SpeechModelsFetchedAt;
        _textModelsFetchedAt = value.TextModelsFetchedAt;
        _activeSecretName = value.SecretName;
        ResolveSelections();
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the configuration as it is at the commit point. A background
    /// refresh and a settings save each change only their own fields, so neither overwrites the other.
    /// </summary>
    private void CommitConfiguration(Func<Configuration, Configuration> change, bool notify = true)
    {
        IPluginHostServices host;
        lock (_configurationLock)
        {
            host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
            var value = change(CaptureConfiguration());
            host.SetSetting("configuration", value);
            ApplyConfiguration(value);
        }

        if (notify) host.NotifyCapabilitiesChanged();
    }

    private async Task CommitWithKeyAsync(Func<Configuration, Configuration> change, string? replacementKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        if (replacementKey is null) { CommitConfiguration(change); return; }
        var normalized = NormalizeApiKey(replacementKey);
        var oldSecret = _activeSecretName;
        var stagedSecret = normalized is null ? null : "api-key-" + Guid.NewGuid().ToString("N");
        var keyChanged = !string.Equals(normalized, _apiKey, StringComparison.Ordinal);
        if (keyChanged)
        {
            // OpenRouter lists are the same for every key, but a refresh started with the old key is dropped.
            lock (_configurationLock) _catalogVersion++;
            _catalogRefresh.Cancel();
        }

        try
        {
            if (stagedSecret is not null) await host.StoreSecretAsync(stagedSecret, normalized!);
            ct.ThrowIfCancellationRequested();
            // The single settings write is the commit point for all fields and the key reference.
            CommitConfiguration(value => change(value) with { SecretName = stagedSecret }, notify: false);
        }
        catch
        {
            if (stagedSecret is not null) await TryDeleteSecretAsync(stagedSecret);
            throw;
        }
        _apiKey = normalized;
        host.NotifyCapabilitiesChanged();
        if (oldSecret is not null) await TryDeleteSecretAsync(oldSecret);
        if (keyChanged) StartCatalogRefreshIfStale();
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
        var changes = new List<Func<Configuration, Configuration>>();
        foreach (var (id, value) in values)
        {
            if (!fields.TryGetValue(id, out var field) || value.Length > field.MaxLength)
                throw new ArgumentException("Invalid OpenRouter setting.");
            // The page sends every field. A model it shows unchanged keeps the saved choice, even when the
            // current list lacks it and the page shows the default instead.
            if (IsUnchangedModel(id, value))
                continue;
            if (field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
                throw new ArgumentException("Invalid OpenRouter setting.");
            switch (id)
            {
                case SelectedTranscriptionModelSettingName:
                    changes.Add(c => c with { SpeechModel = value });
                    break;
                case SelectedLlmModelSettingName:
                    changes.Add(c => c with { TextModel = value, UserSelectedTextModel = true });
                    break;
                case TemperatureModeSettingName:
                    changes.Add(c => c with { TemperatureMode = value });
                    break;
                case TemperatureValueSettingName:
                    var temperature = ParseTemperature(value);
                    changes.Add(c => c with { Temperature = temperature });
                    break;
                default:
                    throw new ArgumentException("Unknown OpenRouter setting.");
            }
        }

        var (draftText, draftSpeech, draftFetchedAt) = (_draftTextModels, _draftSpeechModels, _draftFetchedAt);
        await CommitWithKeyAsync(current =>
        {
            foreach (var change in changes)
                current = change(current);
            // Lists without a draft come from the commit point, so a background refresh is not undone.
            if (draftText is not null)
                current = current with { TextModels = draftText, TextModelsFetchedAt = draftFetchedAt };
            if (draftSpeech is not null)
                current = current with { SpeechModels = draftSpeech, SpeechModelsFetchedAt = draftFetchedAt };
            return current;
        }, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, cancellationToken);
        _draftTextModels = _draftSpeechModels = null;
        _draftFetchedAt = null;
    }

    private bool IsUnchangedModel(string id, string value) => id switch
    {
        // An unmarked text model follows the default; saving it marks it as chosen, as before.
        SelectedLlmModelSettingName => _hasUserSelectedLlmModel
            && (value == _selectedLlmModelId || value == _preferredLlmModelId),
        SelectedTranscriptionModelSettingName => value == _selectedTranscriptionModelId || value == _preferredTranscriptionModelId,
        _ => false,
    };

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
                _draftFetchedAt = _catalogRefresh.UtcNow;
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
