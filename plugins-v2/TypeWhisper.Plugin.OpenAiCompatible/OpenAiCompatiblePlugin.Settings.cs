using System.Globalization;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin
{
    private string _settingsProfileId = DefaultProfileId;
    private readonly Dictionary<string, (string Url, string Version, List<FetchedModel> Models)> _draftCatalogs = new();
    private OpenAiCompatibleProfile SettingsProfile => RequireProfile(_settingsProfileId);

    /// <inheritdoc />
    public bool ShowApiKeySettings => true;
    /// <inheritdoc />
    public string ConnectionIdentity => _settingsProfileId;
    /// <inheritdoc />
    public string ProfileSelectorId => "profile";
    /// <inheritdoc />
    public string? AddProfileActionId => "add";
    /// <inheritdoc />
    public string? RemoveProfileActionId => IsDefaultProfile(_settingsProfileId) ? null : _settingsProfileId + "/delete";
    bool IApiKeyPlugin.IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey(_settingsProfileId));
    Task IApiKeyPlugin.SetApiKeyAsync(string apiKey) => SetApiKeyAsync(apiKey, _settingsProfileId);

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var timeout = CreateRequestTimeoutSource(ct, TimeSpan.FromSeconds(10));
        if (!await ValidateConnectionAsync(_settingsProfileId, timeout.Token))
            throw new InvalidOperationException("Check the saved server URL and optional API key.");
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var profile = SettingsProfile;
            var catalog = _draftCatalogs.TryGetValue(profile.Id, out var draftCatalog) ? draftCatalog.Models : profile.FetchedModels;
            var models = catalog.Select(m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string Id(string name) => profile.Id + "/" + name;
            return
            [
                new("profile", L("Server profile", "Serverprofil"), L("Choose the server to configure. Keys and models are stored separately for each profile.", "Wähle den Server aus. Schlüssel und Modelle werden pro Profil getrennt gespeichert."), profile.Id)
                {
                    Section = PluginSettingsSection.Connection, SaveChoiceOnChange = true,
                    Choices = _profiles.Select(p => new PluginSettingChoice(p.Id, p.DisplayName)).ToArray()
                },
                new(Id("name"), L("Profile name", "Profilname"), L("Shown in model and workflow selections.", "Wird bei der Modell- und Workflow-Auswahl angezeigt."), profile.Name, 100) { Section = PluginSettingsSection.Connection },
                new(Id("url"), L("Server URL", "Server-URL"), L("For example http://localhost:11434. A trailing /v1 is optional. You can test the connection before saving.", "Zum Beispiel http://localhost:11434. Ein abschließendes /v1 ist optional. Du kannst die Verbindung vor dem Speichern testen."), profile.BaseUrl, 2048) { Section = PluginSettingsSection.Connection },
                new(Id("api-version"), L("API version (optional)", "API-Version (optional)"), L("Leave empty unless your server requires a version. Deployment-scoped batch endpoints need a dated version, such as 2025-03-01-preview.", "Leer lassen, wenn der Server keine Version benötigt. Bereitstellungsbezogene Batch-Endpunkte benötigen eine datierte Version, etwa 2025-03-01-preview."), profile.ApiVersion, 100) { Section = PluginSettingsSection.Connection },
                new(Id("transcription"), L("Transcription model ID", "Transkriptionsmodell-ID"), L("Enter the model ID accepted by your server. Leave empty if this server only processes text.", "Gib die Modell-ID deines Servers ein. Für reine Textverarbeitung leer lassen."), profile.SelectedModelId ?? "", 256) { Section = PluginSettingsSection.Transcription, Suggestions = models },
                new(Id("transport"), L("Transcription mode", "Transkriptionsmodus"), L("Auto uses realtime for gpt-live-transcribe and gpt-realtime-whisper; other models use batch. Choose Realtime for custom deployment aliases.", "Automatisch verwendet Echtzeit für gpt-live-transcribe und gpt-realtime-whisper, sonst Batch. Für eigene Echtzeit-Bereitstellungsnamen Echtzeit wählen."), profile.TranscriptionTransport)
                { Section = PluginSettingsSection.Transcription, Choices = [new("auto", L("Automatic", "Automatisch")), new("batch", "Batch"), new("realtime", L("Realtime", "Echtzeit"))] },
                new(Id("realtime-protocol"), L("Realtime protocol", "Echtzeitprotokoll"), L("For deployment aliases, select the underlying model family. Automatic recognizes gpt-realtime-whisper exactly and otherwise uses Live Transcribe.", "Für Bereitstellungsnamen die zugrunde liegende Modellfamilie wählen. Automatisch erkennt gpt-realtime-whisper exakt und verwendet sonst Live Transcribe."), profile.RealtimeProtocol)
                { Section = PluginSettingsSection.Transcription, Choices = [new("auto", L("Automatic", "Automatisch")), new("live", "Live Transcribe"), new("whisper", "Realtime Whisper")], VisibleWhen = new(Id("transport"), ["auto", "realtime"]) },
                new(Id("batch-endpoint"), L("Batch transcription endpoint", "Endpunkt für Batch-Transkription"), L("Deployment-scoped uses /deployments/{model}/audio/transcriptions and requires a dated API version. Realtime always uses /v1/realtime.", "Bereitstellungsbezogen verwendet /deployments/{model}/audio/transcriptions und benötigt eine datierte API-Version. Echtzeit verwendet immer /v1/realtime."), profile.BatchEndpoint)
                { Section = PluginSettingsSection.Transcription, Choices = [new("standard", "Standard v1"), new("deployment-scoped", L("Deployment-scoped", "Bereitstellungsbezogen"))], VisibleWhen = new(Id("transport"), ["auto", "batch"]) },
                new(Id("text"), L("Text model ID", "Textmodell-ID"), L("Default model for text processing. You can enter a model missing from the server catalog.", "Standardmodell für die Textverarbeitung. Modelle außerhalb des Serverkatalogs sind ebenfalls möglich."), profile.SelectedLlmModelId ?? "", 256) { Section = PluginSettingsSection.TextProcessing, Suggestions = models },
                new(Id("llm-api"), L("Text API", "Text-API"), L("Choose the API supported by your server.", "Wähle die API deines Servers."), profile.LlmApi)
                { Section = PluginSettingsSection.TextProcessing, Choices = [new("chat-completions", "Chat Completions"), new("responses", "Responses")] },
                new(Id("reasoning"), L("Reasoning effort", "Reasoning-Stufe"), L("For the Responses API. An explicit effort omits temperature because reasoning models may reject it.", "Für die Responses-API. Bei einer expliziten Stufe wird keine Temperatur gesendet, da Reasoning-Modelle diese ablehnen können."), profile.ReasoningEffort)
                { Section = PluginSettingsSection.TextProcessing, Choices = [new("", L("Provider default", "Anbieterstandard")), new("low", "Low"), new("medium", "Medium"), new("high", "High"), new("xhigh", "X High"), new("max", "Max")], VisibleWhen = new(Id("llm-api"), ["responses"]) },
                new(Id("temperature-mode"), L("Temperature", "Temperatur"), L("Provider default sends no temperature override.", "Anbieterstandard sendet keine Temperaturvorgabe."), profile.TemperatureMode)
                { Section = PluginSettingsSection.TextProcessing, Choices = [new("provider-default", L("Provider default", "Anbieterstandard")), new("custom", L("Custom", "Benutzerdefiniert"))] },
                new(Id("temperature"), L("Temperature value", "Temperaturwert"), L("From 0 to 2. Lower values give more consistent results.", "Von 0 bis 2. Niedrigere Werte liefern gleichmäßigere Ergebnisse."), profile.Temperature.ToString(CultureInfo.InvariantCulture), 16)
                { Section = PluginSettingsSection.TextProcessing, VisibleWhen = new(Id("temperature-mode"), ["custom"]) },
                new(Id("thinking"), L("Thinking mode", "Denkmodus"), L("Uses reasoning_effort for DeepInfra and thinking.type for other compatible servers.", "Verwendet reasoning_effort für DeepInfra und thinking.type für andere kompatible Server."), profile.ThinkingEnabled ? "on" : "off")
                {
                    Section = PluginSettingsSection.TextProcessing,
                    VisibleWhen = new(Id("llm-api"), ["chat-completions"]),
                    Choices = [new("off", L("Off", "Aus")), new("on", L("On", "An"))]
                },
                new(Id("timeout"), L("Text request timeout", "Zeitlimit für Textanfragen"), L("Seconds, from 5 to 3600. Local models may need more time.", "Sekunden, zwischen 5 und 3600. Lokale Modelle benötigen eventuell mehr Zeit."), profile.LlmRequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture), 4) { Section = PluginSettingsSection.TextProcessing }
            ];
        }
    }

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = TextSettings.SingleOrDefault(f => f.Id == id) ?? throw new ArgumentException("Setting is no longer available. Select the profile again.");
        if (value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.");
        if (id == "profile")
        {
            RequireProfile(value);
            _settingsProfileId = value;
            _host?.NotifyCapabilitiesChanged();
            return Task.CompletedTask;
        }

        return SaveProfileSettingsAsync(new Dictionary<string, string> { [id] = value }, cancellationToken);
    }

    /// <summary>Saves non-secret profile fields together, preserving the stored API key.</summary>
    public Task SaveProfileSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        => SaveProfileSettingsAsync(_settingsProfileId, values, null, cancellationToken);

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values,
        string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != _settingsProfileId) throw new ArgumentException("Select the profile again before saving.");
        var profiles = CloneProfiles();
        var profile = profiles.Single(p => p.Id == _settingsProfileId);
        var fields = TextSettings.Where(f => f.Id != ProfileSelectorId).ToDictionary(f => f.Id);
        foreach (var (id, value) in values)
        {
            if (!fields.TryGetValue(id, out var field))
                throw new ArgumentException("Setting is no longer available. Select the profile again.");
            if (value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
                throw new ArgumentException("Invalid setting value for " + field.Title + ".");
            ApplyProfileField(profile, id, value);
        }
        if (profile.BatchEndpoint == "deployment-scoped" && !UsesRealtime(profile) && !IsDatedApiVersion(profile.ApiVersion))
            throw new ArgumentException("Deployment-scoped batch transcription requires a dated API version, such as 2025-03-01-preview.");
        if (_draftCatalogs.TryGetValue(profileId, out var catalog) && catalog.Url == profile.BaseUrl && catalog.Version == profile.ApiVersion)
            profile.FetchedModels = catalog.Models;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(apiKey)) { CommitProfiles(profiles); _draftCatalogs.Remove(profileId); return; }

        // Stage a replacement in encrypted storage. The profile write is the commit point:
        // neither a failed write nor a process exit can replace the preceding active key.
        var oldSecret = SecretKey(profileId);
        profile.ApiKeyRevision = Guid.NewGuid().ToString("N");
        var newSecret = SecretKey(profileId, profile.ApiKeyRevision);
        var normalizedKey = apiKey.Trim();
        QueueSecretCleanup(oldSecret);
        QueueSecretCleanup(newSecret);
        try
        {
            if (_host is not null) await _host.StoreSecretAsync(newSecret, normalizedKey);
            cancellationToken.ThrowIfCancellationRequested();
            CommitProfiles(profiles, notify: false);
        }
        catch
        {
            await RetrySecretCleanupAsync();
            throw;
        }
        _apiKeys[profileId] = normalizedKey;
        _draftCatalogs.Remove(profileId);
        _host?.NotifyCapabilitiesChanged();
        await RetrySecretCleanupAsync();
    }

    private static void ApplyProfileField(OpenAiCompatibleProfile profile, string id, string value)
    {
        switch (id[(id.IndexOf('/') + 1)..])
        {
            case "name":
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter a profile name.");
                profile.Name = value.Trim(); break;
            case "url":
                var url = NormalizeBaseUrl(value);
                if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
                    throw new ArgumentException("Enter an HTTP or HTTPS server URL without credentials, a query, or a fragment.");
                if (profile.BaseUrl != url) profile.FetchedModels = [];
                profile.BaseUrl = url; break;
            case "transcription": profile.SelectedModelId = NullIfWhiteSpace(value); break;
            case "api-version":
                if (profile.ApiVersion != value.Trim()) profile.FetchedModels = [];
                profile.ApiVersion = value.Trim(); break;
            case "transport": profile.TranscriptionTransport = value; break;
            case "realtime-protocol": profile.RealtimeProtocol = value; break;
            case "batch-endpoint": profile.BatchEndpoint = value; break;
            case "llm-api": profile.LlmApi = value; break;
            case "reasoning": profile.ReasoningEffort = value; break;
            case "temperature-mode": profile.TemperatureMode = value; break;
            case "temperature":
                if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var temperature) || !double.IsFinite(temperature) || temperature is < 0 or > 2)
                    throw new ArgumentException("Enter a temperature from 0 to 2 using a decimal point.");
                profile.Temperature = temperature; break;
            case "text": profile.SelectedLlmModelId = NullIfWhiteSpace(value); break;
            case "thinking": profile.ThinkingEnabled = value == "on"; break;
            case "timeout":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 5 or > 3600)
                    throw new ArgumentException("Enter a timeout from 5 to 3600 seconds.");
                profile.LlmRequestTimeoutSeconds = seconds; break;
            default: throw new ArgumentException("Unknown setting.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions
    {
        get
        {
            var actions = new List<PluginSettingsAction>
            {
                new("add", L("Add server profile", "Serverprofil hinzufügen"), L("Creates an empty profile with its own key and model choices.", "Erstellt ein leeres Profil mit eigenem Schlüssel und eigener Modellwahl.")) { Section = PluginSettingsSection.Connection },
                new(_settingsProfileId + "/check", L("Test connection", "Verbindung testen"), L("Tests the entered URL and API key without saving.", "Prüft die eingegebene URL und den API-Schlüssel, ohne zu speichern.")) { Section = PluginSettingsSection.Connection },
                new(_settingsProfileId + "/refresh", L("Refresh models", "Modelle aktualisieren"), L("Fetches models using the entered server URL and API key. Save profile to keep the catalog and your choices.", "Lädt Modelle mit der eingegebenen Server-URL und dem API-Schlüssel. Profil speichern übernimmt den Katalog und deine Auswahl.")) { Section = PluginSettingsSection.Transcription }
            };
            if (!IsDefaultProfile(_settingsProfileId))
                actions.Add(new(_settingsProfileId + "/delete", L("Remove this profile", "Dieses Profil entfernen"), L("Removes this profile and its saved key. Workflows using it will need another provider.", "Entfernt dieses Profil und seinen gespeicherten Schlüssel. Zugehörige Workflows benötigen danach einen anderen Anbieter.")) { Section = PluginSettingsSection.Connection });
            return actions;
        }
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SettingsActions.Any(a => a.Id == id)) throw new ArgumentException("Action is no longer available.");
        var profiles = CloneProfiles();
        if (id == "add")
        {
            var baseName = L("Custom Server", "Eigener Server");
            var name = baseName;
            for (var suffix = 2; profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
                name = baseName + " " + suffix;
            var profile = new OpenAiCompatibleProfile { Id = CreateProfileId(), Name = name };
            profiles.Add(profile);
            CommitProfiles(profiles);
            _settingsProfileId = profile.Id;
            _host?.NotifyCapabilitiesChanged();
            return L("Profile added. Save its server URL and model choices.", "Profil hinzugefügt. Speichere die Server-URL und Modellwahl.");
        }
        if (id.EndsWith("/delete", StringComparison.Ordinal))
        {
            var removedId = _settingsProfileId;
            var removedSecret = SecretKey(removedId);
            // Persist removal first. A storage failure leaves the usable profile intact.
            QueueSecretCleanup(removedSecret);
            profiles.RemoveAll(p => p.Id == removedId);
            CommitProfiles(profiles, notify: false);
            _settingsProfileId = DefaultProfileId;
            _draftCatalogs.Remove(removedId);
            _apiKeys.Remove(removedId);
            _host?.NotifyCapabilitiesChanged();
            await RetrySecretCleanupAsync();
            return L("Profile removed.", "Profil entfernt.");
        }

        using var timeout = CreateRequestTimeoutSource(cancellationToken, TimeSpan.FromSeconds(10));
        if (id.EndsWith("/check", StringComparison.Ordinal))
            return await ValidateConnectionAsync(_settingsProfileId, timeout.Token) ? L("Connection verified.", "Verbindung bestätigt.") : L("Connection failed. Check the server URL and key.", "Verbindung fehlgeschlagen. Prüfe Server-URL und Schlüssel.");
        var models = await FetchModelsAsync(_settingsProfileId, timeout.Token);
        if (models.Count == 0)
            return L("No models returned. Check the server and optional key. Saved models are unchanged.", "Keine Modelle erhalten. Prüfe Server und optionalen Schlüssel. Gespeicherte Modelle bleiben unverändert.");
        profiles.Single(p => p.Id == _settingsProfileId).FetchedModels = models;
        CommitProfiles(profiles);
        return L("Model list refreshed.", "Modellliste aktualisiert.");
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId, IReadOnlyDictionary<string, string> values,
        string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != _settingsProfileId || actionId != profileId + "/check" && actionId != profileId + "/refresh")
            throw new ArgumentException("Select the profile again before testing.");
        var profile = CloneProfiles().Single(p => p.Id == profileId);
        var fields = TextSettings.Where(f => f.Id != ProfileSelectorId).ToDictionary(f => f.Id);
        foreach (var (id, value) in values)
        {
            if (!fields.TryGetValue(id, out var field) || value.Length > field.MaxLength ||
                field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value)) throw new ArgumentException("Invalid profile setting.");
            ApplyProfileField(profile, id, value);
        }
        using var timeout = CreateRequestTimeoutSource(cancellationToken, TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(profile, "/v1/models"));
        Authenticate(request, string.IsNullOrWhiteSpace(apiKey) ? GetApiKey(profileId) : apiKey);
        using var response = await TypeWhisper.PluginSDK.Helpers.OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, timeout.Token);
        if (actionId.EndsWith("/check", StringComparison.Ordinal)) return new(L("Connection verified.", "Verbindung bestätigt."));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var models = ParseModelCatalog(document.RootElement);
        if (models.Count == 0) return new(L("No models returned. You can enter model IDs manually.", "Keine Modelle erhalten. Du kannst Modell-IDs manuell eingeben."));
        cancellationToken.ThrowIfCancellationRequested();
        _draftCatalogs[profileId] = (profile.BaseUrl, profile.ApiVersion, models);
        return new(L($"{models.Count} models found. Choose models and save the profile.", $"{models.Count} Modelle gefunden. Wähle Modelle und speichere das Profil."), true);
    }

    private List<OpenAiCompatibleProfile> CloneProfiles() =>
        JsonSerializer.Deserialize<List<OpenAiCompatibleProfile>>(JsonSerializer.Serialize(_profiles))!;

    private static List<FetchedModel> ParseModelCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(e => new FetchedModel(e.GetProperty("id").GetString()!,
                e.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String ? owner.GetString() : null))
            .Where(m => !string.IsNullOrWhiteSpace(m.Id)).DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void CommitProfiles(List<OpenAiCompatibleProfile> profiles, bool notify = true)
    {
        _host?.SetSetting("profiles", profiles);
        _profiles = profiles;
        if (notify) _host?.NotifyCapabilitiesChanged();
    }

    private static string L(string english, string german) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? german : english;
}
