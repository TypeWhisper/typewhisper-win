using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Speechmatics;

public sealed partial class SpeechmaticsPlugin
{

    internal Func<TimeSpan,CancellationToken,Task> Delay { get; set; } = Task.Delay;
    private string Server => Connection.Get("region","eu")=="us" ? "https://us1.asr.api.speechmatics.com/v2" : "https://asr.api.speechmatics.com/v2";
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => true;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => new(MaxTerms: 100, MaxTotalChars: 4000);
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        Field("model","Accuracy","Genauigkeit","enhanced",PluginSettingsSection.Transcription,new("enhanced","Enhanced"),new("standard","Standard")),
        Field("region","Region","Region","eu",PluginSettingsSection.Connection,new("eu","Europe"),new("us","United States"))
    ];
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio,string? language,bool translate,string? prompt,CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio,translate,false,ct); var key=Connection.RequireKey(); var server=Server;
        var config=new Dictionary<string,object>{["language"]=ProviderConnection.Language(language) ?? "auto",["operating_point"]=SelectedModelId!};
        var terms=ProviderConnection.Terms(prompt); if(terms.Length>0) config["additional_vocab"]=terms.Select(content=>new { content }).ToArray();
        using var submit=Connection.Request(HttpMethod.Post,server+"/jobs",key);
        using var form=new MultipartFormDataContent(); var audio=new ByteArrayContent(wavAudio); audio.Headers.ContentType=new("audio/wav"); form.Add(audio,"data_file","audio.wav");
        form.Add(ProviderConnection.Json(new { type="transcription",transcription_config=config }),"config"); submit.Content=form;
        using var submitted=await Connection.ReadAsync(submit,ct); var id=Uri.EscapeDataString(ProviderConnection.RequiredText(submitted.RootElement,"id"));
        for(var attempt=0;attempt<300;attempt++)
        {
            ct.ThrowIfCancellationRequested(); using var poll=Connection.Request(HttpMethod.Get,server+"/jobs/"+id,key);
            using var response=await Connection.ReadAsync(poll,ct); var job=ProviderConnection.Required(response.RootElement,"job",JsonValueKind.Object);
            var status=ProviderConnection.Text(job,"status");
            if(status=="done")
            {
                using var fetch=Connection.Request(HttpMethod.Get,server+"/jobs/"+id+"/transcript?format=json-v2",key); using var transcript=await Connection.ReadAsync(fetch,ct);
                var results=ProviderConnection.Required(transcript.RootElement,"results",JsonValueKind.Array); var text=new StringBuilder();
                foreach(var item in results.EnumerateArray())
                {
                    var alternatives=ProviderConnection.Required(item,"alternatives",JsonValueKind.Array); if(alternatives.GetArrayLength()==0) continue;
                    var word=ProviderConnection.Text(alternatives[0],"content") ?? throw ProviderConnection.InvalidResponse();
                    if(text.Length>0 && ProviderConnection.Text(item,"type")!="punctuation") text.Append(' '); text.Append(word);
                }
                var detected = ProviderConnection.Language(language);
                if (transcript.RootElement.TryGetProperty("metadata", out var metadata)) detected = ProviderConnection.Text(metadata,"language") ?? detected;
                return new(text.ToString().Trim(),detected,ProviderConnection.Number(job,"duration"),NoSpeechProbability: null);
            }
            if(status is not ("running" or "queued")) throw new PluginRequestException("Speechmatics transcription job failed.",PluginRequestFailureKind.ServerError);
            await Delay(TimeSpan.FromSeconds(2),ct);
        }
        throw new PluginRequestException("Speechmatics transcription timed out.",PluginRequestFailureKind.Timeout);
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request=Connection.Request(HttpMethod.Get,Server+"/jobs?limit=1"); using var response=await Connection.ReadAsync(request,ct);
        _ = ProviderConnection.Required(response.RootElement,"jobs",JsonValueKind.Array);
    }

}
