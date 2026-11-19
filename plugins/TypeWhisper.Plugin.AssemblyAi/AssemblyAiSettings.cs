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

    private string L(string en, string de) => PluginLocalization.Get(_host, en, de);

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(ProfileSelectorId, "AssemblyAI", "", ProviderId)
        { Choices = [new(ProviderId, "AssemblyAI")], Section = PluginSettingsSection.Connection },
        new("selectedModel", L("Transcription model", "Transkriptionsmodell"),
            L("AssemblyAI-STT-3.7 supports 32 languages; with automatic language detection, a language it does not support goes to Universal-2. " +
              "Universal-2 offers 37 languages and always uses the async API, at $0.15/h, without the modes below.",
              "AssemblyAI-STT-3.7 unterstützt 32 Sprachen; mit automatischer Spracherkennung geht eine nicht unterstützte Sprache an Universal-2. " +
              "Universal-2 bietet 37 Sprachen und nutzt immer die Async-API, für 0,15 $/h, ohne die Modi darunter."), _configuration.Model)
        { Choices = TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray(), Section = PluginSettingsSection.Transcription },
        new("transcriptionMode", L("Transcription", "Transkription"),
            L("Live streams dictations with a live preview, and the text is ready right after you stop; billed while the connection is open. " +
              "Recordings, files and dictations without a preview then use the Sync API. " +
              "Sync uploads the recording when you stop and usually finishes in under half a second. " +
              "Dictation uploads dictations when you stop and returns them cleaned up by Universal-3.6 Pro: filler words removed, punctuation and " +
              "capitalization fixed. Recordings and files are not cleaned up; they use the Sync API. Dictations over 20 minutes, with more than " +
              "100 dictionary terms or in a language it doesn't support, and failed requests use the async API without cleanup. " +
              "Async costs the least but takes a few seconds. Only Live shows a live preview from AssemblyAI, and speaker diarization always uses " +
              "the async API. With more than 100 dictionary terms, Live also transcribes the recording, billed in addition. " +
              "AssemblyAI list prices in November 2026; add-ons such as dictionary terms cost extra.",
              "Live streamt Diktate mit Live-Vorschau, und der Text steht direkt nach dem Stoppen bereit; abgerechnet wird, solange die Verbindung offen ist. " +
              "Aufnahmen, Dateien und Diktate ohne Vorschau nutzen dann die Sync-API. " +
              "Sync lädt die Aufnahme beim Stoppen hoch und ist meist in unter einer halben Sekunde fertig. " +
              "Dictation lädt Diktate beim Stoppen hoch und gibt sie von Universal-3.6 Pro bereinigt zurück: Füllwörter entfernt, Satzzeichen und " +
              "Großschreibung korrigiert. Aufnahmen und Dateien werden nicht bereinigt; sie nutzen die Sync-API. Diktate über 20 Minuten, mit mehr " +
              "als 100 Wörterbuchbegriffen oder in einer nicht unterstützten Sprache sowie fehlgeschlagene Anfragen nutzen die Async-API ohne Bereinigung. " +
              "Async kostet am wenigsten, braucht aber einige Sekunden. Nur Live zeigt eine Live-Vorschau von AssemblyAI, und die Sprechertrennung " +
              "nutzt immer die Async-API. Mit mehr als 100 Wörterbuchbegriffen transkribiert Live zusätzlich die Aufnahme, was zusätzlich abgerechnet wird. " +
              "Listenpreise von AssemblyAI im November 2026; Zusatzfunktionen wie Wörterbuchbegriffe kosten extra."),
            Mode)
        {
            Choices =
            [
                new(AssemblyAiModes.Live, LiveModel == AssemblyAiLiveModels.Realtime
                    ? L("Live – $0.30/h", "Live – 0,30 $/h") : L("Live – $0.45/h", "Live – 0,45 $/h")),
                new(AssemblyAiModes.Sync, L("Sync – $0.30/h", "Sync – 0,30 $/h")),
                new(AssemblyAiModes.Dictation, L("Dictation – $0.62/h", "Dictation – 0,62 $/h")),
                new(AssemblyAiModes.Async, L("Async – $0.21/h", "Async – 0,21 $/h"))
            ],
            Section = PluginSettingsSection.Transcription,
            VisibleWhen = new("selectedModel", [AssemblyAiModels.DefaultId])
        },
        new("liveModel", L("Live model", "Live-Modell"),
            L("Realtime-Agent keeps the preview steady as you speak. Realtime costs less, but the preview changes more while you speak. " +
              "The final text is as accurate.",
              "Mit Realtime-Agent bleibt die Vorschau beim Sprechen ruhig. Realtime ist günstiger, aber die Vorschau ändert sich beim Sprechen " +
              "häufiger. Der fertige Text ist genauso gut."),
            LiveModel)
        {
            Choices =
            [
                new(AssemblyAiLiveModels.RealtimeAgent, L("Realtime-Agent – $0.45/h", "Realtime-Agent – 0,45 $/h")),
                new(AssemblyAiLiveModels.Realtime, L("Realtime – $0.30/h", "Realtime – 0,30 $/h"))
            ],
            Section = PluginSettingsSection.Transcription,
            VisibleWhen = new("transcriptionMode", [AssemblyAiModes.Live])
        },
        new("speakerDiarizationEnabled", L("Speaker diarization", "Sprechertrennung"),
            L("Label speakers in recordings, files, watch folders and the HTTP API, with utterance timestamps. Uses the async API; " +
              "dictations are not labeled and then show no live preview from AssemblyAI.",
              "Sprecher in Aufnahmen, Dateien, überwachten Ordnern und der HTTP-API kennzeichnen, mit Zeitstempeln der Äußerungen. Nutzt die " +
              "Async-API; Diktate werden nicht gekennzeichnet und zeigen dann keine Live-Vorschau von AssemblyAI."),
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
                "transcriptionMode" when AssemblyAiModes.All.Contains(value) => next with { Mode = value },
                "liveModel" when AssemblyAiLiveModels.All.Contains(value) => next with { LiveModel = value },
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
