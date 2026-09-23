using TypeWhisper.Plugin.Cohere;
using TypeWhisper.PluginSDK;
public sealed partial class ProviderTests
{
    [Fact]
    public async Task MultipartResponsePreservesTimingLanguageAndSilenceMetadata()
    {
        using var http=new HttpClient(new Handler((request,body)=>Json("""{"text":"Hallo","language":"de","duration":2.5,"segments":[{"text":"Hallo","start":0.5,"end":2.0,"no_speech_prob":0.2}]}""")));
        using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var result=await plugin.TranscribeAsync(Audio(),"en",false,null,default);
        Assert.Equal("de",result.DetectedLanguage);Assert.Equal(2.5,result.DurationSeconds);Assert.Equal(0.2f,result.NoSpeechProbability);Assert.Equal(0.5,Assert.Single(result.Segments).Start);
    }
}
