using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Speechmatics;
public sealed partial class ProviderTests
{

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new SpeechmaticsPlugin();
        var path=Path.Combine(Path.GetTempPath(),"portable-settings-"+Guid.NewGuid().ToString("N"));
        var host=new TypeWhisper.PluginHost.VocabularyHostServices(path);
        await plugin.ActivateAsync(host);
        Assert.NotNull(plugin.TextSettings);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task WrongJsonShapeIsTypedFailure(string body)
    {
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new SpeechmaticsPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new SpeechmaticsPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/v2/jobs",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,request.Method);return Json("""{"jobs":[]}""");}));using var plugin=new SpeechmaticsPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task RegionAccuracyVocabularyAndPunctuationArePreserved()
    {
        using var http=new HttpClient(new Handler((r,b)=>
        {Assert.Equal("us1.asr.api.speechmatics.com",r.RequestUri!.Host);if(r.Method==HttpMethod.Post){Assert.Contains("standard",b);Assert.Contains("additional_vocab",b);Assert.Contains("TypeWhisper",b);}if(r.RequestUri.AbsolutePath.EndsWith("/transcript"))return Json("""{"results":[{"type":"word","alternatives":[{"content":"Hallo"}]},{"type":"punctuation","alternatives":[{"content":"!"}]}]}""");return Success(r,b);}));
        using var plugin=new SpeechmaticsPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);await plugin.SaveTextSettingAsync("region","us",default);plugin.SelectModel("standard");
        Assert.Equal("Hallo!",await Run(plugin));
    }
    [Fact]
    public async Task PollingHonorsCancellation()
    {
        using var cts=new CancellationTokenSource();using var http=new HttpClient(new Handler((r,b)=>r.RequestUri!.AbsolutePath=="/v2/jobs"?Success(r,b):Json("""{"job":{"status":"running"}}""")));
        using var plugin=new SpeechmaticsPlugin(http){Delay=(_,ct)=>{cts.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;}};await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Run(plugin,cts.Token));
    }

}
