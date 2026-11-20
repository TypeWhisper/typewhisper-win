using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Zoom;

public sealed partial class ZoomPlugin
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

    private string L(string en, string de) => PluginLocalization.Get(_host, en, de);

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(ProfileSelectorId, "Zoom Scribe", "", ProviderId)
        { Choices = [new(ProviderId, "Zoom Scribe")], Section = PluginSettingsSection.Connection },
        new("selectedModel", L("Transcription model", "Transkriptionsmodell"),
            L("Scribe Multilingual takes English, German, Spanish, French, Italian, Portuguese, Japanese, Korean, Chinese and Arabic, " +
              "and detects the language when none is set; it does not use dictionary terms. Scribe Single Language uses Zoom's model " +
              "for the dictation language, with dictionary terms, in the same languages except Korean; without a dictation language, " +
              "Scribe Multilingual detects it. Scribe English Pro is Zoom's most accurate English model, with dictionary terms; other " +
              "languages go to Scribe Multilingual.",
              "Scribe Multilingual versteht Englisch, Deutsch, Spanisch, Französisch, Italienisch, Portugiesisch, Japanisch, Koreanisch, " +
              "Chinesisch und Arabisch und erkennt die Sprache, wenn keine eingestellt ist; Wörterbuchbegriffe verwendet es nicht. " +
              "Scribe Single Language nutzt Zooms Modell für die Diktiersprache, mit Wörterbuchbegriffen, in denselben Sprachen außer " +
              "Koreanisch; ohne Diktiersprache erkennt Scribe Multilingual die Sprache. Scribe English Pro ist Zooms genauestes " +
              "englisches Modell, mit Wörterbuchbegriffen; andere Sprachen gehen an Scribe Multilingual."), _configuration.Model)
        { Choices = TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray(), Section = PluginSettingsSection.Transcription },
        new("transcriptionMode", L("Transcription", "Transkription"),
            L("Live streams dictations to Zoom while you speak and shows a live preview; the final text is usually ready half a second " +
              "after you stop. Recordings, files and dictations without a preview are sent when they end. After recording sends every " +
              "recording to Zoom when you stop; the text is usually ready in about a second. No live preview from Zoom.",
              "Live streamt Diktate an Zoom, während du sprichst, und zeigt eine Live-Vorschau; der fertige Text ist meist eine halbe " +
              "Sekunde nach dem Stoppen da. Aufnahmen, Dateien und Diktate ohne Vorschau werden gesendet, wenn sie enden. Nach der " +
              "Aufnahme sendet jede Aufnahme an Zoom, wenn du stoppst; der Text ist meist nach etwa einer Sekunde da. Keine " +
              "Live-Vorschau von Zoom."),
            Mode)
        {
            Choices = [new(ZoomModes.Live, L("Live", "Live")), new(ZoomModes.Batch, L("After recording", "Nach der Aufnahme"))],
            Section = PluginSettingsSection.Transcription
        }
    ];

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("checkConnection", L("Check connection", "Verbindung prüfen"),
            L("Verify the entered key without sending audio or saving changes. Create a key at zoom.ai under Configure > API Keys; " +
              "Zoom bills usage in Build Credits.",
              "Den eingegebenen Schlüssel ohne Audio und ohne Speichern prüfen. Einen Schlüssel erstellst du auf zoom.ai unter " +
              "Configure > API Keys; Zoom rechnet die Nutzung in Build Credits ab."))
        { Section = PluginSettingsSection.Connection }
    ];

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = key.Trim();
        if (value.Length > 1024 || value.Any(char.IsControl)) throw new ArgumentException("Invalid API key format.");
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
        { host.Log(PluginLogLevel.Warning, "An inactive encrypted Zoom Scribe key could not be removed."); }
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
        if (profileId != ProviderId) throw new ArgumentException("Unknown Zoom Scribe configuration.");
        var next = _configuration;
        foreach (var (id, value) in values)
        {
            next = id switch
            {
                "selectedModel" => next with { Model = ZoomModels.Require(value).Id },
                "transcriptionMode" when ZoomModes.All.Contains(value) => next with { Mode = value },
                _ => throw new ArgumentException("Invalid Zoom Scribe setting.")
            };
        }
        // The host's empty replacement field means keep the key; Remove uses SetApiKeyAsync.
        return CommitWithKeyAsync(next, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task ValidateConfigurationAsync(CancellationToken ct) => ValidateKeyAsync(RequireKey(), ct);

    /// <summary>A Fast request for a model that does not exist, with no audio.</summary>
    internal static readonly byte[] KeyCheckBody =
        Encoding.UTF8.GetBytes("{\"file\":\"data:audio/wav;base64,\",\"config\":{\"language\":\"en-US\",\"model\":\"typewhisper-key-check\"}}");

    /// <summary>
    /// Checks a key without transcribing anything: Zoom answers a request for an unknown model with 400 for a valid key and
    /// 401 for an invalid one, so the check costs no credits.
    /// </summary>
    private async Task ValidateKeyAsync(string key, CancellationToken ct)
    {
        using var request = Request(key, KeyCheckBody);
        try { using var _ = await ReadAsync(request, ct, TimeSpan.FromSeconds(20)); }
        catch (PluginRequestException ex) when (ex.HttpStatusCode == 400) { }
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
        if (profileId != ProviderId || actionId != "checkConnection") throw new ArgumentException("Unknown Zoom Scribe action.");
        await ValidateKeyAsync(NormalizeKey(apiKey) ?? RequireKey(), cancellationToken);
        return new(L("Connection verified. No changes saved.", "Verbindung geprüft. Keine Änderungen gespeichert."));
    }
}
