using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

public sealed partial class Reson8Plugin : IApiKeyPlugin, IPluginTextSettings, IPluginSettingsActions
{
    private const string RefreshActionId = "refresh";
    private const string CheckPatternsActionId = "check-patterns";
    private const string DefaultBiasChoice = "default";

    Task IApiKeyPlugin.SetApiKeyAsync(string apiKey) => SetApiKeyAsync(apiKey);
    private string L(string en, string de) => PluginLocalization.Get(_host, en, de);

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured || !await ValidateApiKeyAsync(_apiKey!, ct))
            throw new InvalidOperationException("The API key could not be validated.");

        // A verified key also loads the current models, like saving a key on the Mac.
        try { await RefreshModelListsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        { _host?.Log(PluginLogLevel.Warning, $"Reson8 model lists could not be refreshed after the key check: {ex.Message}"); }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("baseUrl", L("Server URL", "Server-URL"), L("Keep the default unless using a compatible server.", "Behalte den Standard bei, sofern du keinen kompatiblen Server verwendest."), _customBaseUrl) { Section = PluginSettingsSection.Connection },
        new("authHeader", L("Authentication header", "Authentifizierungsheader"), L("Use Authorization for Reson8.", "Verwende Authorization für Reson8."), _customAuthHeader) { Section = PluginSettingsSection.Connection },
        new(FillerModeSettingName, L("Filler words", "Füllwörter"),
            L("Natural: the model gets no instruction, so filler words such as “um” may appear in some places and be left out in others. Clean leaves them out, which suits dictation and notes. Verbatim transcribes them as spoken, which suits interviews. Rumble-2-Flash ignores this setting.",
                "Natürlich: Das Modell erhält keine Vorgabe, daher können Füllwörter wie „äh“ an manchen Stellen erscheinen und an anderen fehlen. Bereinigt lässt sie weg, geeignet für Diktate und Notizen. Wörtlich schreibt sie so mit, wie sie gesprochen werden, geeignet für Interviews. Rumble-2-Flash nutzt diese Einstellung nicht."),
            _fillerMode)
        {
            Section = PluginSettingsSection.Transcription,
            Choices =
            [
                new(Reson8FillerModes.Natural, L("Natural", "Natürlich")),
                new(Reson8FillerModes.Clean, L("Clean", "Bereinigt")),
                new(Reson8FillerModes.Verbatim, L("Verbatim", "Wörtlich")),
            ]
        },
        new(BiasStrengthSettingName, L("Bias strength", "Biasstärke"),
            string.Format(CultureInfo.CurrentCulture, L(
                "How strongly custom models and dictionary terms steer recognition. Higher values recover these terms more often, but can also turn similar-sounding words into them. Default: {0}.",
                "Wie stark eigene Modelle und Wörterbuchbegriffe die Erkennung lenken. Höhere Werte erkennen diese Begriffe häufiger, können aber auch ähnlich klingende Wörter in sie verwandeln. Standard: {0}."),
                DefaultBiasStrength.ToString("0.00", CultureInfo.CurrentCulture)),
            _biasStrength is { } bias ? bias.ToString("0.00", CultureInfo.InvariantCulture) : DefaultBiasChoice)
        {
            Section = PluginSettingsSection.Transcription,
            Choices =
            [
                new(DefaultBiasChoice, string.Format(CultureInfo.CurrentCulture, L("Default ({0})", "Standard ({0})"),
                    DefaultBiasStrength.ToString("0.00", CultureInfo.CurrentCulture))),
                .. Enumerable.Range(0, 21).Select(step => step * 0.05).Where(value => Math.Abs(value - DefaultBiasStrength) >= 0.001)
                    .Select(value => new PluginSettingChoice(value.ToString("0.00", CultureInfo.InvariantCulture), value.ToString("0.00", CultureInfo.CurrentCulture))),
            ]
        },
        new(DictionaryModelEnabledSettingName, L("Keep dictionary in a custom model", "Wörterbuch in einem eigenen Modell führen"),
            L("TypeWhisper keeps your dictionary terms in a custom model named “TypeWhisper Dictionary” in your Reson8 account and updates it when the dictionary changes. Reson8 then takes up to 50,000 terms instead of 250 per request. The model replaces a custom model configured on your API client; with a custom model selected, the terms go as phrases.",
                "TypeWhisper führt deine Wörterbuchbegriffe in einem eigenen Modell namens „TypeWhisper Dictionary“ in deinem Reson8-Konto und aktualisiert es, wenn sich das Wörterbuch ändert. Reson8 berücksichtigt dann bis zu 50.000 Begriffe statt 250 pro Anfrage. Das Modell ersetzt ein eigenes Modell, das für deinen API-Client eingerichtet ist; ist ein eigenes Modell ausgewählt, gehen die Begriffe als Phrasen mit."),
            _dictionaryModelEnabled ? "true" : "false")
        {
            Section = PluginSettingsSection.Transcription,
            Choices = [new("true", L("On", "Ein")), new("false", L("Off", "Aus"))]
        },
        new(PatternSetsSettingName, L("Pattern sets", "Mustergruppen"),
            L("Patterns recover structured tokens such as order codes or reference numbers. Each set appears as a model, so a workflow can use it where such tokens are spoken; Reson8 advises against applying patterns everywhere. Write the set's name on one line and one pattern per line below it, and separate sets with an empty line. [0-9] is a digit, [A-Z] a letter, {n} or {m,n} repeats what comes before, (…)? marks an optional part. Example: Order codes, then AMZ[0-9]{6}. Patterns replace dictionary terms and do not work with an API client that has a custom model configured.",
                "Muster erkennen strukturierte Angaben wie Bestellnummern oder Aktenzeichen. Jede Gruppe erscheint als Modell, sodass ein Workflow sie dort nutzen kann, wo solche Angaben gesprochen werden; Reson8 rät davon ab, Muster überall zu verwenden. Schreib den Namen der Gruppe in eine Zeile und darunter ein Muster pro Zeile, und trenne Gruppen mit einer Leerzeile. [0-9] ist eine Ziffer, [A-Z] ein Buchstabe, {n} oder {m,n} wiederholt das Vorangehende, (…)? kennzeichnet einen optionalen Teil. Beispiel: Bestellnummern, darunter AMZ[0-9]{6}. Muster ersetzen Wörterbuchbegriffe und funktionieren nicht mit einem API-Client, für den ein eigenes Modell eingerichtet ist."),
            Reson8Catalog.FormatPatternSets(_patternSets), 16_000)
        {
            Section = PluginSettingsSection.Transcription,
            IsMultiline = true
        },
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || !setting.IsMultiline && (value.Contains('\r') || value.Contains('\n'))
            || setting.Choices.Count > 0 && !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id)
        {
            case "baseUrl": SetCustomBaseUrl(value); break;
            case "authHeader": SetCustomAuthHeader(value); break;
            case FillerModeSettingName: SetFillerMode(value); break;
            case BiasStrengthSettingName:
                SetBiasStrength(value == DefaultBiasChoice ? null : double.Parse(value, CultureInfo.InvariantCulture));
                break;
            case DictionaryModelEnabledSettingName: SetDictionaryModelEnabled(value == "true"); break;
            case PatternSetsSettingName: SetPatternSets(Reson8Catalog.ParsePatternSets(value, _patternSets)); break;
            default: throw new ArgumentException("Unknown setting.", nameof(id));
        }
        return Task.CompletedTask;
    }

    internal void SetFillerMode(string mode)
    {
        var normalized = Reson8FillerModes.Normalize(mode);
        _host?.SetSetting(FillerModeSettingName, normalized);
        _fillerMode = normalized;
    }

    internal void SetBiasStrength(double? strength)
    {
        var normalized = NormalizeBiasStrength(strength);
        _host?.SetSetting(BiasStrengthSettingName, normalized);
        _biasStrength = normalized;
    }

    internal void SetDictionaryModelEnabled(bool enabled)
    {
        _host?.SetSetting(DictionaryModelEnabledSettingName, enabled);
        _dictionaryModelEnabled = enabled;
        // The dictionary budget changes.
        _host?.NotifyCapabilitiesChanged();
    }

    /// <summary>Saves the pattern sets, which are models; a selected set that was removed falls back to the default model.</summary>
    internal void SetPatternSets(IReadOnlyList<Reson8PatternSet> sets)
    {
        var sanitized = Reson8Catalog.SanitizePatternSets(sets);
        _host?.SetSetting(PatternSetsSettingName, sanitized);
        _patternSets = sanitized;
        if (_selectedModelId.StartsWith(Reson8PatternSet.ModelIdPrefix, StringComparison.Ordinal) && PatternSetFor(_selectedModelId) is null)
            SelectModel(DefaultModelId);
        _host?.NotifyCapabilitiesChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new(RefreshActionId, L("Refresh models", "Modelle aktualisieren"),
            L("Loads Reson8's models and the custom models available to this API key.", "Lädt die Modelle von Reson8 und die eigenen Modelle, die mit diesem API-Schlüssel verfügbar sind."))
        { Section = PluginSettingsSection.Transcription },
        new(CheckPatternsActionId, L("Check patterns with Reson8", "Muster bei Reson8 prüfen"),
            L("Sends half a second of silence with each saved pattern set, so Reson8 checks the patterns.", "Sendet mit jeder gespeicherten Mustergruppe eine halbe Sekunde Stille, damit Reson8 die Muster prüft."))
        { Section = PluginSettingsSection.Transcription },
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new InvalidOperationException("Store an API key first.");
        switch (id)
        {
            case RefreshActionId:
                var refresh = await RefreshModelListsAsync(ct);
                var loaded = string.Format(CultureInfo.CurrentCulture,
                    L("{0} models and {1} custom models loaded.", "{0} Modelle und {1} eigene Modelle geladen."), refresh.Models, refresh.CustomModels);
                return refresh.Failure is null ? loaded : loaded + " " + string.Format(CultureInfo.CurrentCulture,
                    L("One list could not be loaded and keeps its previous entries: {0}", "Eine Liste konnte nicht geladen werden und behält ihre bisherigen Einträge: {0}"),
                    refresh.Failure);
            case CheckPatternsActionId:
                return await CheckPatternSetsAsync(ct);
            default:
                throw new ArgumentException("Unknown action.", nameof(id));
        }
    }

    /// <summary>
    /// Fetches both lists. A list that cannot be fetched keeps the one fetched before, and with it the selection;
    /// a proxy without <c>/v1/model</c> still loads its custom models.
    /// </summary>
    internal async Task<(int Models, int CustomModels, string? Failure)> RefreshModelListsAsync(CancellationToken ct)
    {
        IReadOnlyList<Reson8Model>? models = null;
        IReadOnlyList<Reson8CustomModel>? customModels = null;
        Exception? failure = null;
        try { models = await FetchModelsAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { failure = ex; }
        try { customModels = await FetchCustomModelsAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { failure ??= ex; }
        if (models is null && customModels is null)
            throw new InvalidOperationException($"Reson8 models could not be loaded: {failure?.Message}", failure);

        if (customModels is not null)
            SetFetchedCustomModels(customModels);
        if (models is not null)
            SetFetchedModels(models);
        return (models?.Count ?? _fetchedModels.Count, CustomModels.Count(), failure?.Message);
    }

    /// <summary>
    /// Sends half a second of silence with each pattern set. Reson8 checks the syntax and refuses patterns for an
    /// API client with a custom model configured, and names its reason.
    /// </summary>
    internal async Task<string> CheckPatternSetsAsync(CancellationToken ct)
    {
        if (_patternSets.Count == 0)
            return L("Save a pattern set first.", "Speichere zuerst eine Mustergruppe.");
        var results = new List<string>();
        foreach (var set in _patternSets)
        {
            var query = new Reson8Query()
                .Add("encoding", "pcm_s16le").Add("sample_rate", "16000").Add("channels", "1")
                .Add("patterns", set.QueryValue ?? "");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl(_customBaseUrl)}/v1/speech-to-text/prerecorded?{query}");
            AddAuthHeader(request, _apiKey!, _customAuthHeader);
            request.Content = new ByteArrayContent(new byte[16_000]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            string outcome;
            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                outcome = response.IsSuccessStatusCode
                    ? L("Reson8 accepts these patterns.", "Reson8 akzeptiert diese Muster.")
                    : ExtractApiError(await response.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException ex)
            {
                outcome = ex.Message;
            }

            results.Add($"{set.Name}: {outcome}");
        }

        return string.Join("\n", results);
    }
}
