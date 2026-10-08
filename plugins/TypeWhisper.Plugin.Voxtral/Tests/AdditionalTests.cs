using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Voxtral;
public sealed partial class ProviderTests
{

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new VoxtralPlugin();
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
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new VoxtralPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new VoxtralPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/v1/models",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,request.Method);return Json("""{"data":[]}""");}));using var plugin=new VoxtralPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task SelectModelAsyncWaitsForAContendedSaveWithoutBlocking()
    {
        using var plugin=new VoxtralPlugin(); var host=new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StoreSecretDelay=release.Task;
        // The key save holds the connection gate while the secret store is pending.
        var keySave=plugin.SetApiKeyAsync("replacement-key");
        var select=plugin.SelectModelAsync(VoxtralPlugin.RealtimeModel,default);
        Assert.False(keySave.IsCompleted); Assert.False(select.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(keySave,select).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VoxtralPlugin.RealtimeModel,plugin.SelectedModelId);
        Assert.Equal(VoxtralPlugin.RealtimeModel,host.Settings["configuration"].GetProperty("Values").GetProperty("model").GetString());
        await Assert.ThrowsAsync<ArgumentException>(()=>plugin.SelectModelAsync("not-a-model",default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>plugin.SelectModelAsync("voxtral-mini-latest",new(true)));
        Assert.Equal(VoxtralPlugin.RealtimeModel,plugin.SelectedModelId);
    }
}
