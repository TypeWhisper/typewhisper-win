using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Fireworks;
public sealed partial class ProviderTests
{

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new FireworksPlugin();
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
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/inference/v1/models",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,request.Method);return Json("""{"models":[]}""");}));using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task LlmPersistsSelectionTemperatureAndHonorsWorkflowOverride()
    {
        string? body=null;using var http=new HttpClient(new Handler((r,b)=>{Assert.Equal("/inference/v1/chat/completions",r.RequestUri!.AbsolutePath);body=b;return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"Ergebnis"}}]}""");}));
        var host=new Host();using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(host);await Configure(plugin);
        await plugin.SaveTextSettingAsync("llmModel","custom-model",default);await plugin.SaveTextSettingAsync("temperature","0.6",default);await plugin.SaveTextSettingAsync("temperatureMode","custom",default);
        await plugin.DeactivateAsync();await plugin.ActivateAsync(host);Assert.Equal("Ergebnis",await plugin.ProcessAsync("Rewrite","Grüße","",default));
        using(var doc=JsonDocument.Parse(body!)){Assert.Equal("custom-model",doc.RootElement.GetProperty("model").GetString());Assert.Equal(0.6,doc.RootElement.GetProperty("temperature").GetDouble());Assert.Equal("Grüße",doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());}
        await plugin.SaveTextSettingAsync("temperatureMode","providerDefault",default);await plugin.ProcessAsync("","text","workflow-model",default);
        using var final=JsonDocument.Parse(body!);Assert.Equal("workflow-model",final.RootElement.GetProperty("model").GetString());Assert.False(final.RootElement.TryGetProperty("temperature",out _));
    }
    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task LlmRejectsPartialAnswers(string reason)
    {
        using var http=new HttpClient(new Handler((_,_)=>Json(JsonSerializer.Serialize(new {choices=new[]{new {finish_reason=reason,message=new {content="partial"}}}}))));
        using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>plugin.ProcessAsync("","test","",default));
    }
    [Theory]
    [InlineData("NaN")][InlineData("Infinity")][InlineData("-1")][InlineData("2.1")]
    public async Task InvalidTemperatureDoesNotPersist(string value)
    {
        using var plugin=new FireworksPlugin();var host=new Host();await plugin.ActivateAsync(host);
        await Assert.ThrowsAsync<ArgumentException>(()=>plugin.SaveTextSettingAsync("temperature",value,default));Assert.Empty(host.Settings);
    }

    [Fact]
    public async Task TurboTranslationUsesDedicatedAudioEndpointAndPrompt()
    {
        using var http=new HttpClient(new Handler((r,b)=>{Assert.Equal("audio-turbo.api.fireworks.ai",r.RequestUri!.Host);Assert.Equal("/v1/audio/translations",r.RequestUri.AbsolutePath);Assert.DoesNotContain((MultipartFormDataContent)r.Content!, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "language");Assert.Contains("whisper-v3-turbo",b);Assert.Contains("TypeWhisper",b);return Json("""{"text":"Hello"}""");}));
        using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);plugin.SelectModel("whisper-v3-turbo");
        Assert.Equal("Hello",(await plugin.TranscribeAsync(Audio(),"de",true,"TypeWhisper",default)).Text);
    }
    [Fact]
    public async Task ModelRefreshAcceptsNativeCatalogAndKeepsManualSelection()
    {
        using var http=new HttpClient(new Handler((_,_)=>Json("""{"models":[{"name":"accounts/fireworks/models/new-chat"},{"name":"accounts/fireworks/models/whisper-v3"}]}""")));
        using var plugin=new FireworksPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);await plugin.SaveTextSettingAsync("llmModel","manual-deployment",default);
        await plugin.ExecuteSettingsActionAsync("refreshModels",default);Assert.Contains(plugin.SupportedModels,m=>m.Id=="accounts/fireworks/models/new-chat");Assert.DoesNotContain(plugin.SupportedModels,m=>m.Id.Contains("whisper"));Assert.Equal("manual-deployment",plugin.TextSettings.Single(f=>f.Id=="llmModel").Value);
    }

}
