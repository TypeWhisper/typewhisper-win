using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cohere;

public sealed partial class CoherePlugin
{

    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public int MaximumAudioUploadBytes => 25 * 1024 * 1024;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => ["ar","de","el","en","es","fr","it","ja","ko","nl","pl","pt","vi","zh"];
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public bool IsAvailable => IsConfigured;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => ProviderConnection.ValidModelId(Connection.Get("llmModel","command-a-03-2025"))
        ? [new(Connection.Get("llmModel","command-a-03-2025"),"Command / custom model")] : [];
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        Field("language", "Default transcription language", "Standardsprache für Transkription", "en", PluginSettingsSection.Transcription, SupportedLanguages.Select(l => new PluginSettingChoice(l,l)).ToArray()),
        Field("llmModel", "Text model ID", "Textmodell-ID", "command-a-03-2025", PluginSettingsSection.TextProcessing),
        Field("temperatureMode", "Temperature", "Temperatur", "providerDefault", PluginSettingsSection.TextProcessing, new("providerDefault","Provider default"),new("custom","Custom")),
        Field("temperature", "Custom temperature (0–2)", "Eigene Temperatur (0–2)", "0.3", PluginSettingsSection.TextProcessing) with { VisibleWhen = new("temperatureMode",["custom"]) }
    ];
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct);
        if (wavAudio.Length > MaximumAudioUploadBytes) throw new PluginRequestException("Cohere audio uploads must not exceed 25 MB.", PluginRequestFailureKind.RequestTooLarge);
        var explicitLanguage = ProviderConnection.Language(language);
        var lang = (explicitLanguage ?? ProviderConnection.Language(Connection.Get("language","en")))?.Split('-','_')[0].ToLowerInvariant();
        if(lang is null || !SupportedLanguages.Contains(lang))
        {
            if (explicitLanguage is null) throw new PluginRequestException("The saved Cohere transcription language is unsupported.", PluginRequestFailureKind.Configuration);
            throw new ArgumentException("Unsupported Cohere transcription language.");
        }
        return Connection.MultipartAsync("https://api.cohere.com/v2/audio/transcriptions",SelectedModelId!,wavAudio,lang,null,ct);
    }
    /// <inheritdoc />
    public Task<string> ProcessAsync(string systemPrompt,string userText,string model,CancellationToken ct) => Connection.ChatAsync(
        "https://api.cohere.com/compatibility/v1/chat/completions",string.IsNullOrWhiteSpace(model) ? RequireSavedModel() : model.Trim(),systemPrompt,userText,ct);
    private string RequireSavedModel()
    {
        var model = Connection.Get("llmModel", "command-a-03-2025");
        if (!ProviderConnection.ValidModelId(model)) throw new PluginRequestException("The saved Cohere text model ID is invalid.", PluginRequestFailureKind.Configuration);
        return model;
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request = Connection.Request(HttpMethod.Get,"https://api.cohere.com/v1/models");
        using var response = await Connection.ReadAsync(request,ct); _ = ProviderConnection.Required(response.RootElement,"models",JsonValueKind.Array);
    }

}
