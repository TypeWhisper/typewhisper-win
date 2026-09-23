using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Linear;
public sealed partial class ProviderTests
{

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new LinearPlugin();
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
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new LinearPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new LinearPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/graphql",request.RequestUri!.AbsolutePath);Assert.Contains("viewer",body);Assert.DoesNotContain("mutation",body);Assert.Equal("fixture-key",Assert.Single(request.Headers.GetValues("Authorization")));return Json("""{"data":{"viewer":{"id":"fixture-user"}}}""");}));using var plugin=new LinearPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task GraphQlErrorsFailAndNeverReturnSuccessUrl()
    {
        using var http=new HttpClient(new Handler((_,_)=>Json("""{"errors":[{"message":"denied"}],"data":null}""")));using var plugin=new LinearPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task MissingTeamAndEmptyTextDoNotCreateIssues()
    {
        var calls=0;using var http=new HttpClient(new Handler((_,_)=>{calls++;return Json("{}");}));using var plugin=new LinearPlugin(http);await plugin.ActivateAsync(new Host());await plugin.SetApiKeyAsync("fixture");Assert.True(plugin.IsConfigured);
        Assert.False((await plugin.ExecuteAsync("Text",new(null,null,null,null,null),default)).Success);await Configure(plugin);
        Assert.False((await plugin.ExecuteAsync(" ",new(null,null,null,null,null),default)).Success);Assert.Equal(0,calls);
    }

}
