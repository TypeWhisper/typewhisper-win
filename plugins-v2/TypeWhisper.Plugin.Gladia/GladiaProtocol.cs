using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gladia;

public sealed partial class GladiaPlugin
{

    internal Func<TimeSpan,CancellationToken,Task> Delay { get; set; } = Task.Delay;
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => new(MaxTerms: 100, MaxTotalChars: 4000);
    /// <inheritdoc />
    public bool SupportsLanguageHints => true;
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [];
    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(wavAudio,ProviderConnection.Language(language) is { } l ? [l] : [],translate,prompt,ct);
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(byte[] wavAudio,IReadOnlyList<string> languageHints,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct); var key=Connection.RequireKey();
        using var upload=Connection.Request(HttpMethod.Post,"https://api.gladia.io/v2/upload",key,"x-gladia-key");
        using var form=new MultipartFormDataContent(); var audio=new ByteArrayContent(wavAudio); audio.Headers.ContentType=new("audio/wav"); form.Add(audio,"audio","audio.wav"); upload.Content=form;
        using var uploaded=await Connection.ReadAsync(upload,ct); var audioUrl=ProviderConnection.RequiredText(uploaded.RootElement,"audio_url");
        var body=new Dictionary<string,object>{["audio_url"]=audioUrl};
        var languages=languageHints.Select(ProviderConnection.Language).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(languages.Length>0) body["language_config"]=new { languages, code_switching=languages.Length>1 };
        var terms=ProviderConnection.Terms(prompt);
        if(terms.Length>0) { body["custom_vocabulary"]=true; body["custom_vocabulary_config"]=new { vocabulary=terms, default_intensity=0.7 }; }
        using var submit=Connection.Request(HttpMethod.Post,"https://api.gladia.io/v2/pre-recorded",key,"x-gladia-key"); submit.Content=ProviderConnection.Json(body);
        using var job=await Connection.ReadAsync(submit,ct);
        var resultUrl=ProviderConnection.RequiredText(job.RootElement,"result_url");
        if(!Uri.TryCreate(resultUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host!="api.gladia.io" || uri.UserInfo.Length!=0 || !uri.AbsolutePath.StartsWith("/v2/pre-recorded/",StringComparison.Ordinal)) throw ProviderConnection.InvalidResponse();
        for(var attempt=0;attempt<300;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var poll=Connection.Request(HttpMethod.Get,resultUrl,key,"x-gladia-key"); using var document=await Connection.ReadAsync(poll,ct);
            var root=document.RootElement; var status=ProviderConnection.Text(root,"status");
            if(status=="done")
            {
                var transcription=ProviderConnection.Required(ProviderConnection.Required(root,"result",JsonValueKind.Object),"transcription",JsonValueKind.Object);
                var text=ProviderConnection.Text(transcription,"full_transcript") ?? throw ProviderConnection.InvalidResponse();
                var detected = languages.FirstOrDefault();
                if (transcription.TryGetProperty("languages", out var detectedLanguages) && detectedLanguages.ValueKind == JsonValueKind.Array && detectedLanguages.GetArrayLength() > 0 && detectedLanguages[0].ValueKind == JsonValueKind.String) detected = detectedLanguages[0].GetString();
                return new(text.Trim(), detected, ProviderConnection.Number(transcription,"duration"), NoSpeechProbability: null);
            }
            if(status is not ("queued" or "processing")) throw new PluginRequestException("Gladia transcription job failed.",PluginRequestFailureKind.ServerError);
            await Delay(TimeSpan.FromSeconds(2),ct);
        }
        throw new PluginRequestException("Gladia transcription timed out.",PluginRequestFailureKind.Timeout);
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request=Connection.Request(HttpMethod.Get,"https://api.gladia.io/v2/pre-recorded?limit=1",header:"x-gladia-key");
        using var response=await Connection.ReadAsync(request,ct);
        if(response.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw ProviderConnection.InvalidResponse();
    }

}
