using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed partial class AssemblyAiPlugin
{
    /// <inheritdoc />
    public string ProfileSelectorId => "configurationProfile";
    /// <inheritdoc />
    public string? AddProfileActionId => null;
    /// <inheritdoc />
    public string? RemoveProfileActionId => null;
    /// <inheritdoc />
    public bool ShowApiKeySettings => true;
    /// <inheritdoc />
    public string ConnectionIdentity => ProviderId;

    private string L(string en, string de)
    {
        try { return _host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en; }
        catch (NotSupportedException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en; }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(ProfileSelectorId, "AssemblyAI", "", ProviderId)
        { Choices = [new(ProviderId, "AssemblyAI")], Section = PluginSettingsSection.Connection },
        new("selectedModel", L("Transcription model", "Transkriptionsmodell"),
            L("Universal-3.5 Pro supports 18 languages. Universal-2 offers wider recorded-audio language support.",
              "Universal-3.5 Pro unterstützt 18 Sprachen. Universal-2 bietet bei Aufnahmen eine größere Sprachauswahl."), _configuration.Model)
        { Choices = TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray(), Section = PluginSettingsSection.Transcription },
        new("speakerDiarizationEnabled", L("Speaker diarization", "Sprechertrennung"),
            L("Label speakers and preserve utterance timestamps. Uses recorded-audio transcription instead of live text.",
              "Sprecher kennzeichnen und Zeitstempel der Äußerungen erhalten. Verwendet Transkription nach der Aufnahme statt Live-Text."),
            _configuration.SpeakerDiarizationEnabled ? "true" : "false")
        { Choices = [new("false", L("Off", "Aus")), new("true", L("On", "An"))], Section = PluginSettingsSection.Transcription }
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("checkConnection", L("Check connection", "Verbindung prüfen"),
            L("Verify the entered key without uploading audio or saving changes.", "Den eingegebenen Key ohne Audio-Upload oder Speichern prüfen."))
        { Section = PluginSettingsSection.Connection }
    ];

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = key.Trim();
        if (value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("Invalid API key format.");
        return value;
    }

    private void Commit(Configuration value, bool notify = true)
    {
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        host.SetSetting("configuration", value);
        _configuration = value;
        if (notify) host.NotifyCapabilitiesChanged();
    }

    private async Task CommitWithKeyAsync(Configuration value, string? replacementKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        if (replacementKey is null) { Commit(value); return; }
        var key = NormalizeKey(replacementKey);
        var oldSecret = _configuration.SecretName;
        var stagedSecret = key is null ? null : "api-key-" + Guid.NewGuid().ToString("N");
        try
        {
            if (stagedSecret is not null) await host.StoreSecretAsync(stagedSecret, key!);
            ct.ThrowIfCancellationRequested();
            // All fields and the encrypted-key reference change at one settings commit point.
            Commit(value with { SecretName = stagedSecret }, notify: false);
        }
        catch
        {
            if (stagedSecret is not null) await DeleteInactiveSecretAsync(host, stagedSecret);
            throw;
        }
        _apiKey = key;
        host.NotifyCapabilitiesChanged();
        if (oldSecret is not null) await DeleteInactiveSecretAsync(host, oldSecret);
    }

    private static async Task DeleteInactiveSecretAsync(IPluginHostServices host, string name)
    {
        try { await host.DeleteSecretAsync(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { host.Log(PluginLogLevel.Warning, "An inactive encrypted AssemblyAI key could not be removed."); }
    }

    /// <inheritdoc />
    public Task SetApiKeyAsync(string apiKey) => CommitWithKeyAsync(_configuration, apiKey, default);
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken) =>
        id == ProfileSelectorId && value == ProviderId ? Task.CompletedTask :
        SaveProfileSettingsAsync(ProviderId, new Dictionary<string, string> { [id] = value }, null, cancellationToken);

    /// <inheritdoc />
    public Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId != ProviderId) throw new ArgumentException("Unknown AssemblyAI configuration.");
        var next = _configuration;
        foreach (var (id, value) in values)
        {
            next = id switch
            {
                "selectedModel" => next with { Model = AssemblyAiModels.Require(value).Id },
                "speakerDiarizationEnabled" when value is "true" or "false" => next with { SpeakerDiarizationEnabled = value == "true" },
                _ => throw new ArgumentException("Invalid AssemblyAI setting.")
            };
        }
        // The host's empty replacement field means keep the key; Remove uses SetApiKeyAsync.
        return CommitWithKeyAsync(next, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task ValidateConfigurationAsync(CancellationToken ct) => ValidateKeyAsync(RequireKey(), ct);

    private async Task ValidateKeyAsync(string key, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/v2/transcript?limit=1", key);
        using var result = await ReadAsync(request, ct);
        if (!result.RootElement.TryGetProperty("transcripts", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw InvalidResponse();
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken) =>
        (await ExecuteProfileActionAsync(ProviderId, id, new Dictionary<string, string>(), null, cancellationToken)).Message;

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId,
        IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        if (profileId != ProviderId || actionId != "checkConnection") throw new ArgumentException("Unknown AssemblyAI action.");
        await ValidateKeyAsync(NormalizeKey(apiKey) ?? RequireKey(), cancellationToken);
        return new(L("Connection verified. No changes saved.", "Verbindung geprüft. Keine Änderungen gespeichert."));
    }
}
