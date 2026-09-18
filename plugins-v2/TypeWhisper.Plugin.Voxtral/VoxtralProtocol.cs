using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{

    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [];
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct);
        return Connection.MultipartAsync("https://api.mistral.ai/v1/audio/transcriptions",SelectedModelId!,wavAudio,language,null,ct);
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request = Connection.Request(HttpMethod.Get,"https://api.mistral.ai/v1/models");
        using var response = await Connection.ReadAsync(request,ct); _ = ProviderConnection.Required(response.RootElement,"data",JsonValueKind.Array);
    }

}
