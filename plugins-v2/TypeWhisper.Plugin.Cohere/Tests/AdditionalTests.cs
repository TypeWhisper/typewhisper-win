using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Cohere;
public sealed partial class ProviderTests
{
    [Fact]
    public async Task TranscriptionFieldsPrecedeFileAndChatBudgetFitsCommandA()
    {
        using var http = new HttpClient(new Handler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/transcriptions"))
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                Assert.Equal(new[] { "model", "language", "file" }, form.Select(part => part.Headers.ContentDisposition!.Name!.Trim('"')));
                return Json("""{"text":"Hallo"}""");
            }
            using var doc = JsonDocument.Parse(body!);
            Assert.InRange(doc.RootElement.GetProperty("max_tokens").GetInt32(), 1, 8192);
            return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"Hallo"}}]}""");
        }));
        using var plugin = new CoherePlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await plugin.TranscribeAsync(Audio(), "de", false, null, default);
        await plugin.ProcessAsync("Correct spelling", "hallo", "", default);
    }
    [Theory]
    [InlineData("it")]
    [InlineData("el")]
    [InlineData("nl")]
    [InlineData("pl")]
    [InlineData("vi")]
    public async Task DocumentedTranscribeLanguagesReachTheApi(string language)
    {
        using var http = new HttpClient(new Handler((request, body) =>
        {
            Assert.Contains(language, body); return Json("""{"text":"fixture"}""");
        }));
        using var plugin = new CoherePlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        Assert.Contains(language, plugin.SupportedLanguages);
        Assert.Equal("fixture", (await plugin.TranscribeAsync(Audio(), language, false, null, default)).Text);
    }

    [Fact]
    public async Task OversizedAudioIsRejectedBeforeAnyHttpRequest()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request")));
        using var plugin = new CoherePlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(new byte[plugin.MaximumAudioUploadBytes + 1], "de", false, null, default));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, error.FailureKind);
        Assert.DoesNotContain("ru", plugin.SupportedLanguages);
    }

    [Fact]
    public async Task SettingsRenderThroughActualPortableHostServices()
    {
        using var plugin=new CoherePlugin();
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
        using var http=new HttpClient(new Handler((_,_)=>Json(body)));using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task NetworkFailureIsTyped()
    {
        using var http=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("offline")));using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        var ex=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(PluginRequestFailureKind.Network,ex.FailureKind);
    }
    [Fact]
    public async Task ConnectionCheckUsesReadOnlyEndpoint()
    {
        using var http=new HttpClient(new Handler((request,body)=>{Assert.Equal("/v1/models",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Get,request.Method);return Json("""{"models":[]}""");}));using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task LlmPersistsSelectionTemperatureAndHonorsWorkflowOverride()
    {
        string? body=null;using var http=new HttpClient(new Handler((r,b)=>{Assert.Equal("/compatibility/v1/chat/completions",r.RequestUri!.AbsolutePath);body=b;return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"Ergebnis"}}]}""");}));
        var host=new Host();using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(host);await Configure(plugin);
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
        using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>plugin.ProcessAsync("","test","",default));
    }
    [Theory]
    [InlineData("NaN")][InlineData("Infinity")][InlineData("-1")][InlineData("2.1")]
    public async Task InvalidTemperatureDoesNotPersist(string value)
    {
        using var plugin=new CoherePlugin();var host=new Host();await plugin.ActivateAsync(host);
        await Assert.ThrowsAsync<ArgumentException>(()=>plugin.SaveTextSettingAsync("temperature",value,default));Assert.Empty(host.Settings);
    }

    [Fact]
    public async Task AutoLanguageUsesExplicitSavedLanguageAndRejectsUnsupportedLanguage()
    {
        using var http=new HttpClient(new Handler((r,b)=>{Assert.Contains("de",b);Assert.DoesNotContain("prompt",b);return Json("""{"text":"Hallo"}""");}));
        using var plugin=new CoherePlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);await plugin.SaveTextSettingAsync("language","de",default);
        await plugin.TranscribeAsync(Audio(),"auto",false,"Ignored vocabulary",default);
        await Assert.ThrowsAsync<ArgumentException>(()=>plugin.TranscribeAsync(Audio(),"xx",false,null,default));
    }

    [Theory]
    [InlineData(257)]
    [InlineData(2048)]
    public async Task OverlongModelIdDoesNotReplaceSavedModel(int length)
    {
        using var plugin = new CoherePlugin(); var host = new Host();
        await plugin.ActivateAsync(host);
        var valid = new string('a', 256);
        await plugin.SaveTextSettingAsync("llmModel", valid, default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("llmModel", new string('b', length), default));
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(valid, Assert.Single(plugin.SupportedModels).Id);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    [InlineData("2.1")]
    public async Task InvalidRestoredTemperatureFailsBeforeHttp(string temperature)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request")));
        using var plugin = new CoherePlugin(http); var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        var configuration = host.Settings["configuration"].Deserialize<ProviderConnection.Configuration>()!;
        host.Settings["configuration"] = JsonSerializer.SerializeToElement(configuration with
        {
            Values = new Dictionary<string, string> { ["temperatureMode"] = "custom", ["temperature"] = temperature }
        });
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("Rewrite", "Hallo", "", default));
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
    }

}
