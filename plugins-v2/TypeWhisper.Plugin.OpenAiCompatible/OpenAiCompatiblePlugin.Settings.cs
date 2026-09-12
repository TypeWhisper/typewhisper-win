using System.Globalization;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin
{
    private string _settingsProfileId = DefaultProfileId;
    private OpenAiCompatibleProfile SettingsProfile => RequireProfile(_settingsProfileId);

    /// <inheritdoc />
    public bool ShowApiKeySettings => true;
    /// <inheritdoc />
    public string ConnectionIdentity => _settingsProfileId;
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
            string Id(string name) => profile.Id + "/" + name;
            return
            [
                new("profile", L("Server profile", "Serverprofil"), L("Choose the server to configure. Keys and models are stored separately for each profile.", "Wähle den Server aus. Schlüssel und Modelle werden pro Profil getrennt gespeichert."), profile.Id)
                {
                    Section = PluginSettingsSection.Connection, SaveChoiceOnChange = true,
                    Choices = _profiles.Select(p => new PluginSettingChoice(p.Id, p.DisplayName)).ToArray()
                },
                new(Id("name"), L("Profile name", "Profilname"), L("Shown in model and workflow selections.", "Wird bei der Modell- und Workflow-Auswahl angezeigt."), profile.Name, 100) { Section = PluginSettingsSection.Connection },
                new(Id("url"), L("Server URL", "Server-URL"), L("For example http://localhost:11434. A trailing /v1 is optional. Save before checking the connection.", "Zum Beispiel http://localhost:11434. Ein abschließendes /v1 ist optional. Vor dem Verbindungstest speichern."), profile.BaseUrl, 2048) { Section = PluginSettingsSection.Connection },
                new(Id("transcription"), L("Transcription model ID", "Transkriptionsmodell-ID"), L("Enter the model ID accepted by your server. Leave empty if this server only processes text.", "Gib die Modell-ID deines Servers ein. Für reine Textverarbeitung leer lassen."), profile.SelectedModelId ?? "", 256) { Section = PluginSettingsSection.Transcription },
                new(Id("text"), L("Text model ID", "Textmodell-ID"), L("Default model for text processing. You can enter a model missing from the server catalog.", "Standardmodell für die Textverarbeitung. Modelle außerhalb des Serverkatalogs sind ebenfalls möglich."), profile.SelectedLlmModelId ?? "", 256) { Section = PluginSettingsSection.TextProcessing },
                new(Id("thinking"), L("Thinking mode", "Denkmodus"), L("Uses reasoning_effort for DeepInfra and thinking.type for other compatible servers.", "Verwendet reasoning_effort für DeepInfra und thinking.type für andere kompatible Server."), profile.ThinkingEnabled ? "on" : "off")
                {
                    Section = PluginSettingsSection.TextProcessing,
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

        // Only publish the edited profile after the durable write succeeds.
        var profiles = CloneProfiles();
        var profile = profiles.Single(p => p.Id == _settingsProfileId);
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
            case "text": profile.SelectedLlmModelId = NullIfWhiteSpace(value); break;
            case "thinking": profile.ThinkingEnabled = value == "on"; break;
            case "timeout":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 5 or > 3600)
                    throw new ArgumentException("Enter a timeout from 5 to 3600 seconds.");
                profile.LlmRequestTimeoutSeconds = seconds; break;
            default: throw new ArgumentException("Unknown setting.");
        }
        CommitProfiles(profiles);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions
    {
        get
        {
            var actions = new List<PluginSettingsAction>
            {
                new("add", L("Add server profile", "Serverprofil hinzufügen"), L("Creates an empty profile with its own key and model choices.", "Erstellt ein leeres Profil mit eigenem Schlüssel und eigener Modellwahl.")) { Section = PluginSettingsSection.Connection },
                new(_settingsProfileId + "/refresh", L("Refresh models", "Modelle aktualisieren"), L("Fetches the model catalog from the saved server URL. Existing choices are retained.", "Lädt den Modellkatalog von der gespeicherten Server-URL. Die Modellwahl bleibt erhalten.")) { Section = PluginSettingsSection.Connection }
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
            var profile = new OpenAiCompatibleProfile { Id = CreateProfileId(), Name = L("Custom Server", "Eigener Server") };
            profiles.Add(profile);
            CommitProfiles(profiles);
            _settingsProfileId = profile.Id;
            _host?.NotifyCapabilitiesChanged();
            return L("Profile added. Save its server URL and model choices.", "Profil hinzugefügt. Speichere die Server-URL und Modellwahl.");
        }
        if (id.EndsWith("/delete", StringComparison.Ordinal))
        {
            var removedId = _settingsProfileId;
            // Persist removal first. A storage failure leaves the usable profile intact.
            profiles.RemoveAll(p => p.Id == removedId);
            CommitProfiles(profiles, notify: false);
            _settingsProfileId = DefaultProfileId;
            try { if (_host is not null) await _host.DeleteSecretAsync(SecretKey(removedId)); }
            finally { _apiKeys.Remove(removedId); _host?.NotifyCapabilitiesChanged(); }
            return L("Profile removed.", "Profil entfernt.");
        }

        using var timeout = CreateRequestTimeoutSource(cancellationToken, TimeSpan.FromSeconds(10));
        var models = await FetchModelsAsync(_settingsProfileId, timeout.Token);
        if (models.Count == 0)
            return L("No models returned. Check the server and optional key. Saved models are unchanged.", "Keine Modelle erhalten. Prüfe Server und optionalen Schlüssel. Gespeicherte Modelle bleiben unverändert.");
        profiles.Single(p => p.Id == _settingsProfileId).FetchedModels = models;
        CommitProfiles(profiles);
        return L("Model list refreshed.", "Modellliste aktualisiert.");
    }

    private List<OpenAiCompatibleProfile> CloneProfiles() =>
        JsonSerializer.Deserialize<List<OpenAiCompatibleProfile>>(JsonSerializer.Serialize(_profiles))!;

    private void CommitProfiles(List<OpenAiCompatibleProfile> profiles, bool notify = true)
    {
        _host?.SetSetting("profiles", profiles);
        _profiles = profiles;
        if (notify) _host?.NotifyCapabilitiesChanged();
    }

    private static string L(string english, string german) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? german : english;
}
