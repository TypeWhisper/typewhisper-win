using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Gladia;
public sealed partial class ProviderTests
{
    [Fact]
    public async Task LiveSessionIsAuthenticatedAndConnectsOnlyToValidatedEndpoint()
    {
        using var http = new HttpClient(new Handler((request, body) =>
        {
            Assert.Equal("https://api.gladia.io/v2/live", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("x-gladia-key")));
            using var doc = JsonDocument.Parse(body!);
            Assert.Equal("TypeWhisper", doc.RootElement.GetProperty("realtime_processing").GetProperty("custom_vocabulary_config").GetProperty("vocabulary")[0].GetString());
            return Json("""{"id":"fixture","url":"wss://api.gladia.io/v2/live?token=fixture"}""");
        }));
        using var plugin = new GladiaPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var connected = false;
        plugin.ConnectStreaming = (uri, ct) =>
        {
            Assert.Equal("wss://api.gladia.io/v2/live?token=fixture", uri.AbsoluteUri);
            ct.ThrowIfCancellationRequested(); connected = true;
            return Task.FromResult<IStreamingSession>(null!);
        };
        await plugin.StartStreamingWithLanguageHintsAndPromptAsync(["de", "en"], "TypeWhisper", default);
        Assert.True(connected);
    }

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new GladiaPlugin();
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
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new GladiaPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new GladiaPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/v2/pre-recorded",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,request.Method);return Json("""{"items":[]}""");}));using var plugin=new GladiaPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task UploadAndPollingPreserveLanguageHintsAndVocabulary()
    {
        var polls=0;using var http=new HttpClient(new Handler((r,b)=>
        {
            if(r.RequestUri!.AbsolutePath=="/v2/pre-recorded")
            {using var doc=JsonDocument.Parse(b!);var root=doc.RootElement;Assert.Equal("TypeWhisper",root.GetProperty("custom_vocabulary_config").GetProperty("vocabulary")[0].GetString());Assert.True(root.GetProperty("language_config").GetProperty("code_switching").GetBoolean());}
            if(r.RequestUri.AbsolutePath == PollPath && polls++==0)return Json("""{"status":"processing"}""");return Success(r,b);
        }));using var plugin=new GladiaPlugin(http){Delay=(_,ct)=>Task.CompletedTask};await plugin.ActivateAsync(new Host());await Configure(plugin);
        Assert.Equal("Hallo Welt",(await plugin.TranscribeWithLanguageHintsAsync(Audio(),["de","en"],false,"TypeWhisper",default)).Text);Assert.Equal(2,polls);
    }
    [Theory]
    [InlineData("https://api.gladia.io/v2/transcription/")]
    [InlineData("https://attacker.invalid/")]
    public async Task PollUsesCanonicalJobIdAndReturnsMetadataDuration(string resultBase)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, body) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath == "/v2/pre-recorded")
                return Json(JsonSerializer.Serialize(new { id = JobId, result_url = resultBase + JobId }));
            Assert.Equal("api.gladia.io", request.RequestUri.Host);
            return Success(request, body);
        }));
        using var plugin = new GladiaPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var result = await plugin.TranscribeAsync(Audio(), "de", false, null, default);
        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal(3.125, result.DurationSeconds);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../another-job")]
    [InlineData("https://attacker.invalid/")]
    public async Task InvalidJobIdNeverStartsPolling(string? jobId)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, body) =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath == "/v2/upload"
                ? Success(request, body)
                : Json(JsonSerializer.Serialize(new { id = jobId, result_url = "https://api.gladia.io" + PollPath }));
        }));
        using var plugin = new GladiaPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(() => Run(plugin));
        Assert.Equal(2, calls);
    }
}
