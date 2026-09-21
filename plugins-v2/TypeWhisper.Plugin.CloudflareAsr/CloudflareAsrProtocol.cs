using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed partial class CloudflareAsrPlugin
{

    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    // Conservative baseline from Cloudflare's Free/Pro request-body ceiling.
    public int MaximumAudioUploadBytes => IsTurbo ? 74_000_000 : 100_000_000;
    private bool IsTurbo => SelectedModelId == "whisper-large-v3-turbo";
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => IsTurbo ? WhisperLanguages : [];
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => IsTurbo;
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [Field("accountId","Cloudflare account ID","Cloudflare-Konto-ID","",PluginSettingsSection.Connection)];
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct);
        if (wavAudio.Length > MaximumAudioUploadBytes)
            throw new PluginRequestException("Recording exceeds this model’s upload limit. Use a shorter recording.", PluginRequestFailureKind.RequestTooLarge);
        var selectedLanguage = ProviderConnection.Language(language);
        if (!IsTurbo && selectedLanguage is not null)
            throw new NotSupportedException("This Cloudflare model supports automatic language detection only.");
        if(!IsConfigured) throw new PluginRequestException("Account ID and API token required.",PluginRequestFailureKind.Configuration);
        var account = Connection.Get("accountId"); ValidateValue("accountId",account);
        using var request = Connection.Request(HttpMethod.Post,$"https://api.cloudflare.com/client/v4/accounts/{account}/ai/run/@cf/openai/{SelectedModelId}");
        if (IsTurbo)
        {
            if (selectedLanguage is not null && !WhisperLanguages.Contains(selectedLanguage))
                throw new ArgumentException("Choose a supported spoken language.", nameof(language));
            var payload = new Dictionary<string, object> { ["audio"] = Convert.ToBase64String(wavAudio), ["task"] = "transcribe" };
            if (selectedLanguage is not null) payload["language"] = selectedLanguage;
            var terms = ProviderConnection.Terms(prompt);
            if (terms.Length > 0) payload["initial_prompt"] = string.Join(", ", terms);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new ByteArrayContent(wavAudio);
            request.Content.Headers.ContentType = new("application/octet-stream");
        }
        using var document = await Connection.ReadAsync(request,ct); var root=document.RootElement;
        _ = ProviderConnection.Required(root, "success", JsonValueKind.True);
        var result=ProviderConnection.Required(root,"result",JsonValueKind.Object);
        var info = result.TryGetProperty("transcription_info", out var details) && details.ValueKind == JsonValueKind.Object ? details : result;
        return new(ProviderConnection.Text(result,"text")?.Trim() ?? throw ProviderConnection.InvalidResponse(),ProviderConnection.Text(info,"language"),ProviderConnection.Number(info,"duration"), NoSpeechProbability: null);
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured)
            throw new PluginRequestException("Account ID and API token required.", PluginRequestFailureKind.Configuration);
        try { ValidateValue("accountId", Connection.Get("accountId")); }
        catch (ArgumentException) { throw new PluginRequestException("A valid Cloudflare account ID is required.", PluginRequestFailureKind.Configuration); }
        using var request = Connection.Request(HttpMethod.Get,$"https://api.cloudflare.com/client/v4/accounts/{Connection.Get("accountId")}/ai/models/search?per_page=1");
        using var result = await Connection.ReadAsync(request,ct);
        _ = ProviderConnection.Required(result.RootElement, "success", JsonValueKind.True);
        _ = ProviderConnection.Required(result.RootElement, "result", JsonValueKind.Array);
    }

    private static readonly IReadOnlyList<string> WhisperLanguages = Array.AsReadOnly(new[]
    {
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi",
        "fi", "vi", "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la",
        "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy",
        "ne", "mn", "bs", "kk", "sq", "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be",
        "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl",
        "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su"
    });
}
