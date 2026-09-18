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
    public IReadOnlyList<PluginTextSetting> TextSettings => [Field("accountId","Cloudflare account ID","Cloudflare-Konto-ID","",PluginSettingsSection.Connection)];
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct);
        if(!IsConfigured) throw new PluginRequestException("Account ID and API token required.",PluginRequestFailureKind.Configuration);
        var account = Connection.Get("accountId"); ValidateValue("accountId",account);
        using var request = Connection.Request(HttpMethod.Post,$"https://api.cloudflare.com/client/v4/accounts/{account}/ai/run/@cf/openai/whisper");
        request.Content = new ByteArrayContent(wavAudio); request.Content.Headers.ContentType = new("application/octet-stream");
        using var document = await Connection.ReadAsync(request,ct); var root=document.RootElement;
        if(root.TryGetProperty("success",out var success) && success.ValueKind == JsonValueKind.False) throw ProviderConnection.InvalidResponse();
        var result=ProviderConnection.Required(root,"result",JsonValueKind.Object);
        return new(ProviderConnection.Text(result,"text")?.Trim() ?? throw ProviderConnection.InvalidResponse(),ProviderConnection.Text(result,"language"),ProviderConnection.Number(result,"duration"), NoSpeechProbability: null);
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured)
            throw new PluginRequestException("Account ID and API token required.", PluginRequestFailureKind.Configuration);
        try { ValidateValue("accountId", Connection.Get("accountId")); }
        catch (ArgumentException) { throw new PluginRequestException("A valid Cloudflare account ID is required.", PluginRequestFailureKind.Configuration); }
        using var request = Connection.Request(HttpMethod.Get,"https://api.cloudflare.com/client/v4/user/tokens/verify");
        using var result = await Connection.ReadAsync(request,ct);
        if(ProviderConnection.Text(ProviderConnection.Required(result.RootElement,"result",JsonValueKind.Object),"status") != "active") throw ProviderConnection.InvalidResponse();
    }

}
