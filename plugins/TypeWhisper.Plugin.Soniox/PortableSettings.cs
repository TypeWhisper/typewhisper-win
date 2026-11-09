using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Soniox;

public sealed partial class SonioxPlugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
{
    private const string RefreshModelsActionId = "refresh-models";

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

        // A verified key also loads the current models and voices, as checking a key does on the Mac.
        // A background refresh would only repeat the request.
        _catalogRefresh.Cancel();
        try
        {
            await RefreshCatalogAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _host?.Log(PluginLogLevel.Warning, $"Soniox model list refresh after the key check failed: {ex.Message}");
        }
    }

    private string L(string english, string german) => PluginLocalization.Get(_host, english, german);

    private string RegionTitle(SonioxRegion region) => region.Id switch
    {
        "us" => L("United States", "Vereinigte Staaten"),
        "eu" => L("European Union", "Europäische Union"),
        "jp" => L("Japan", "Japan"),
        _ => region.DisplayName
    };

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var catalog = CurrentCatalog;
            return
            [
                new(RegionSettingKey, L("Region", "Region"),
                    L("Choose the region of the Soniox project your API key belongs to.",
                        "Wähle die Region des Soniox-Projekts, zu dem dein API-Key gehört."), _region)
                {
                    Section = PluginSettingsSection.Connection,
                    Choices = AvailableRegions.Select(region => new PluginSettingChoice(region.Id, RegionTitle(region))).ToArray()
                },
                new(ModelSettingKey, L("Model", "Modell"),
                    L("Automatic uses Soniox's newest model for live text and recordings. Choose a version to stay on it.",
                        "Automatisch nutzt das neueste Soniox-Modell für Live-Text und Aufnahmen. Wähle eine Version, um dabei zu bleiben."),
                    _modelSelection)
                {
                    Section = PluginSettingsSection.Transcription,
                    Choices = ModelChoices(catalog)
                },
                new(ContextSettingKey, L("Transcription context", "Transkriptionskontext"),
                    L("Optional background such as topic, names or setting. It is sent to Soniox with every transcription; dictionary terms are added automatically.",
                        "Optionaler Hintergrund wie Thema, Namen oder Situation. Er wird bei jeder Transkription an Soniox gesendet; Wörterbuchbegriffe kommen automatisch dazu."),
                    _transcriptionContext, MaximumContextTextCharacters)
                {
                    Section = PluginSettingsSection.Transcription,
                    IsMultiline = true
                },
                new(TtsModelSettingKey, L("Speech model", "Sprachausgabe-Modell"),
                    L("Automatic uses Soniox's newest text-to-speech model.",
                        "Automatisch nutzt das neueste Sprachausgabe-Modell von Soniox."),
                    _ttsModelSelection)
                {
                    Section = PluginSettingsSection.Speech,
                    Choices = TtsModelChoices(catalog)
                },
                new(VoiceSettingKey, L("Default speech voice", "Standardstimme"),
                    L("Used when no voice is selected under Advanced > Spoken feedback > Voice.",
                        "Wird verwendet, wenn unter „Erweitert“ > „Sprachfeedback“ > „Stimme“ keine Stimme gewählt ist."),
                    SelectedVoiceId ?? DefaultVoiceId)
                {
                    Section = PluginSettingsSection.Speech,
                    Choices = SpeechVoices().Select(voice => new PluginSettingChoice(voice, voice)).ToArray()
                },
            ];
        }
    }

    private PluginSettingChoice[] ModelChoices(SonioxCatalog? catalog)
    {
        var automatic = SonioxModels.VersionKey(SonioxModels.PreferredSttModelId(catalog?.SttModels, SonioxModels.RealtimeMode));
        var versions = SonioxModels.SelectableVersions(catalog?.SttModels).ToList();
        if (versions.Count == 0)
            versions.Add(SonioxModels.VersionKey(SonioxModels.FallbackRealtimeModelId)!);
        if (_modelSelection != SonioxModels.Automatic && !versions.Contains(_modelSelection))
            versions.Add(_modelSelection);

        return
        [
            new(SonioxModels.Automatic, string.Format(CultureInfo.CurrentCulture,
                L("Automatic (currently {0})", "Automatisch (derzeit {0})"), automatic)),
            .. versions.Select(version => new PluginSettingChoice(version, version))
        ];
    }

    private PluginSettingChoice[] TtsModelChoices(SonioxCatalog? catalog)
    {
        var models = (catalog?.TtsModels ?? []).Where(model => model.AliasedModelId is null)
            .OrderByDescending(model => model.Id, SonioxModels.ModelIdComparer.Instance)
            .Select(model => new PluginSettingChoice(model.Id, model.Name ?? model.Id)).ToList();
        if (models.Count == 0)
            models.Add(new PluginSettingChoice(SonioxModels.FallbackTtsModelId, SonioxModels.FallbackTtsModelId));
        if (_ttsModelSelection != SonioxModels.Automatic && models.All(model => model.Value != _ttsModelSelection))
            models.Add(new PluginSettingChoice(_ttsModelSelection, _ttsModelSelection));

        return
        [
            new(SonioxModels.Automatic, string.Format(CultureInfo.CurrentCulture,
                L("Automatic (currently {0})", "Automatisch (derzeit {0})"),
                SonioxModels.PreferredTtsModelId(catalog?.TtsModels))),
            .. models
        ];
    }

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || !setting.IsMultiline && (value.Contains('\r') || value.Contains('\n')))
            throw new ArgumentException("Invalid setting value.", nameof(value));

        switch (id)
        {
            case RegionSettingKey:
                RequireChoice(setting, value);
                SetRegion(value);
                break;
            // A model saved together with a new region may not be listed for that region yet; the format check
            // is enough, because Soniox names every mode of a version the same way.
            case ModelSettingKey:
                SetModelSelection(value);
                break;
            case ContextSettingKey:
                SetTranscriptionContext(value);
                break;
            case TtsModelSettingKey:
                SelectTtsModel(value);
                break;
            case VoiceSettingKey:
                SelectVoice(value);
                break;
            default:
                throw new ArgumentException("Unknown setting.", nameof(id));
        }

        _host?.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }

    private static void RequireChoice(PluginTextSetting setting, string value)
    {
        if (!setting.Choices.Any(choice => choice.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
    }

    internal void SetModelSelection(string selection)
    {
        var trimmed = selection.Trim();
        var normalized = trimmed == SonioxModels.Automatic
            ? trimmed
            : SonioxModels.SelectionVersion(trimmed) ?? throw new ArgumentException("Unknown Soniox model.", nameof(selection));
        _host?.SetSetting(ModelSettingKey, normalized);
        _modelSelection = normalized;
    }

    internal void SetTranscriptionContext(string text)
    {
        var normalized = NormalizeContextText(text) ?? "";
        _host?.SetSetting(ContextSettingKey, normalized);
        _transcriptionContext = normalized;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new(RefreshModelsActionId, L("Refresh models", "Modelle aktualisieren"),
            L("Loads the current transcription models and voices for the selected region from Soniox.",
                "Lädt die aktuellen Transkriptionsmodelle und Stimmen für die gewählte Region von Soniox."))
        {
            Section = PluginSettingsSection.Transcription
        }
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != RefreshModelsActionId)
            throw new ArgumentException("Unknown settings action.", nameof(id));
        if (!IsConfigured)
            throw new InvalidOperationException(L("Save a Soniox API key first.", "Speichere zuerst einen Soniox-API-Key."));

        // The button loads the same lists; a background refresh would only repeat the request.
        _catalogRefresh.Cancel();
        var catalog = await RefreshCatalogAsync(cancellationToken);
        return string.Format(CultureInfo.CurrentCulture,
            L("Models refreshed. Automatic uses {0} for live text and {1} for recordings.",
                "Modelle aktualisiert. Automatisch nutzt {0} für Live-Text und {1} für Aufnahmen."),
            SonioxModels.PreferredSttModelId(catalog.SttModels, SonioxModels.RealtimeMode),
            SonioxModels.PreferredSttModelId(catalog.SttModels, SonioxModels.AsyncMode));
    }
}
