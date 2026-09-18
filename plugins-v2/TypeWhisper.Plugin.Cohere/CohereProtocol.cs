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
    public IReadOnlyList<string> SupportedLanguages => ["ar","bn","de","en","es","fr","hi","ja","ko","pt","ru","sw","tr","zh"];
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public bool IsAvailable => IsConfigured;
    /// <inheritdoc />
    public bool SupportsRequestHedging => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => [new(Connection.Get("llmModel","command-a-03-2025"),"Command / custom model")];
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        Field("language", "Default transcription language", "Standardsprache fÃ¼r Transkription", "en", PluginSettingsSection.Transcription, SupportedLanguages.Select(l => new PluginSettingChoice(l,l)).ToArray()),
        Field("llmModel", "Text model ID", "Textmodell-ID", "command-a-03-2025", PluginSettingsSection.TextProcessing),
        Field("temperatureMode", "Temperature", "Temperatur", "providerDefault", PluginSettingsSection.TextProcessing, new("providerDefault","Provider default"),new("custom","Custom")),
        Field("temperature", "Custom temperature (0â€“2)", "Eigene Temperatur (0â€“2)", "0.3", PluginSettingsSection.TextProcessing) with { VisibleWhen = new("temperatureMode",["custom"]) }
    ];
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct);
        var lang = (ProviderConnection.Language(language) ?? Connection.Get("language","en")).Split('-','_')[0].ToLowerInvariant();
        if(!SupportedLanguages.Contains(lang)) throw new ArgumentException("Unsupported Cohere transcription language.");
        return Connection.MultipartAsync("https://api.cohere.com/v2/audio/transcriptions",SelectedModelId!,wavAudio,lang,null,ct);
    }
    /// <inheritdoc />
    public Task<string> ProcessAsync(string systemPrompt,string userText,string model,CancellationToken ct) => Connection.ChatAsync(
        "https://api.cohere.com/compatibility/v1/chat/completions",string.IsNullOrWhiteSpace(model) ? Connection.Get("llmModel","command-a-03-2025") : model.Trim(),systemPrompt,userText,ct);
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request = Connection.Request(HttpMethod.Get,"https://api.cohere.com/v1/models");
        using var response = await Connection.ReadAsync(request,ct); _ = ProviderConnection.Required(response.RootElement,"models",JsonValueKind.Array);
    }

}
