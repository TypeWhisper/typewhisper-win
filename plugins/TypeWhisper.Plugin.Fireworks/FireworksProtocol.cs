using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Fireworks;

public sealed partial class FireworksPlugin : IPluginSettingsActions
{

    /// <inheritdoc />
    public bool SupportsTranslation => true;
    /// <inheritdoc />
    public bool SupportsStructuredDictionaryTerms => true;
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => new(MaxTerms: 100, MaxTotalChars: 4000);
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public bool IsAvailable => IsConfigured;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    private static readonly string[] Defaults = ["accounts/fireworks/models/gpt-oss-120b"];
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => (Connection.Get("catalog").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Concat(string.IsNullOrWhiteSpace(Connection.Get("catalog")) ? Defaults : [])
        .Prepend(Connection.Get("llmModel", Defaults[0]))).Distinct().Select((m, index) => new PluginModelInfo(m,m) { IsRecommended = index == 0 }).ToArray();
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        Field("model", "Transcription model", "Transkriptionsmodell", SelectedModelId!, PluginSettingsSection.Transcription, TranscriptionModels.Select(m => new PluginSettingChoice(m.Id,m.DisplayName)).ToArray()) with
        {
            Description = Connection.L("Fireworks deprecated its audio API. Turbo currently works; Whisper V3 may reject valid keys. Live transcription is unavailable.",
                "Fireworks hat seine Audio-API abgekündigt. Turbo funktioniert derzeit; Whisper V3 kann gültige Schlüssel ablehnen. Live-Transkription ist nicht verfügbar.")
        },
        Field("llmModel", "Text model ID", "Textmodell-ID", Defaults[0], PluginSettingsSection.TextProcessing) with { Suggestions = SupportedModels.Select(m => m.Id).ToArray() },
        Field("temperatureMode", "Temperature", "Temperatur", "providerDefault", PluginSettingsSection.TextProcessing, new("providerDefault", "Provider default"), new("custom", "Custom")),
        Field("temperature", "Custom temperature (0–2)", "Eigene Temperatur (0–2)", "0.3", PluginSettingsSection.TextProcessing) with { VisibleWhen = new("temperatureMode", ["custom"]) }
    ];
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio, translate, SupportsTranslation, ct);
        var server = SelectedModelId == "whisper-v3-turbo" ? "audio-turbo" : "audio-prod";
        return Connection.MultipartAsync($"https://{server}.api.fireworks.ai/v1/audio/{(translate ? "translations" : "transcriptions")}", SelectedModelId!, wavAudio, translate ? null : language, string.Join(", ", ProviderConnection.Terms(prompt)), ct, responseFormat: "verbose_json");
    }
    /// <inheritdoc />
    public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
        Connection.ChatAsync("https://api.fireworks.ai/inference/v1/chat/completions", string.IsNullOrWhiteSpace(model) ? Connection.Get("llmModel", Defaults[0]) : model.Trim(), systemPrompt, userText, ct);
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request = Connection.Request(HttpMethod.Get, "https://api.fireworks.ai/inference/v1/models?page_size=1");
        using var result = await Connection.ReadAsync(request, ct);
        var root = result.RootElement;
        var entries = root.TryGetProperty("models", out var models) ? models : ProviderConnection.Required(root, "data", JsonValueKind.Array);
        if (entries.ValueKind != JsonValueKind.Array) throw ProviderConnection.InvalidResponse();
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("refreshModels", Connection.L("Refresh models", "Modelle aktualisieren"), Connection.L("Save the model catalog using the stored key.", "Modellkatalog mit dem gespeicherten Key abrufen."))];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if(id != "refreshModels") throw new ArgumentException("Unknown action.");
        var key = Connection.RequireKey(); var models = new List<string>(); var tokens = new HashSet<string>(); string? token = null;
        for (var page = 0; page < 100; page++)
        {
            var url = "https://api.fireworks.ai/inference/v1/models?page_size=200" + (token is null ? "" : "&page_token=" + Uri.EscapeDataString(token));
            using var request = Connection.Request(HttpMethod.Get,url,key); using var json = await Connection.ReadAsync(request,cancellationToken);
            var root = json.RootElement; var native = root.TryGetProperty("models",out var entries);
            if (!native) entries = ProviderConnection.Required(root,"data",JsonValueKind.Array);
            if(entries.ValueKind != JsonValueKind.Array) throw ProviderConnection.InvalidResponse();
            foreach(var entry in entries.EnumerateArray())
            {
                var model = ProviderConnection.RequiredText(entry,native ? "name" : "id");
                if (string.Equals(ProviderConnection.Text(entry, "kind"), "EMBEDDING_MODEL", StringComparison.OrdinalIgnoreCase)
                    || (entry.TryGetProperty("supports_chat", out var chat) && chat.ValueKind == JsonValueKind.False)) continue;
                if (model.Length <= 256 && !model.Any(char.IsWhiteSpace) && !new[]{"whisper","embedding","reranker","image","stable-diffusion"}.Any(x => model.Contains(x,StringComparison.OrdinalIgnoreCase))) models.Add(model);
            }
            token = ProviderConnection.Text(root,"nextPageToken") ?? ProviderConnection.Text(root,"next_page_token");
            if(string.IsNullOrWhiteSpace(token)) break;
            if(!tokens.Add(token) || page == 99) throw ProviderConnection.InvalidResponse();
        }
        if(models.Count == 0 || Connection.Key != key) throw ProviderConnection.InvalidResponse();
        await Connection.SaveAsync("catalog",string.Join('\n',models.Distinct().Order()),cancellationToken);
        return Connection.L("Models updated.","Modelle aktualisiert.");
    }

}
