using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.CloudflareAsr;
public sealed partial class ProviderTests
{

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new CloudflareAsrPlugin();
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
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new CloudflareAsrPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new CloudflareAsrPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/client/v4/user/tokens/verify",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,request.Method);return Json("""{"result":{"status":"active"}}""");}));using var plugin=new CloudflareAsrPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Theory]
    [InlineData("../other")][InlineData("xyz")]
    public async Task AccountIdCannotChangeRequestPath(string value)
    {using var plugin=new CloudflareAsrPlugin();await plugin.ActivateAsync(new Host());await Assert.ThrowsAsync<ArgumentException>(()=>plugin.SaveTextSettingAsync("accountId",value,default));}
    [Fact]
    public async Task ProviderFailureIsNotAnEmptySuccessfulTranscript()
    {
        using var http=new HttpClient(new Handler((_,_)=>Json("""{"success":false,"errors":[{"message":"denied"}]}""")));using var plugin=new CloudflareAsrPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }

}
